using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// The north Meadows connection: Birch/Oak bends north around existing homes, east, then south into Trampoline Ct.
/// Added to the existing authored town without rebuilding houses, stop IDs, inventories or saves. All peers build the
/// same static geometry; the cloned DistrictGate retains the original business-local permit and walking rules.
/// </summary>
public sealed partial class NeighborhoodConnection : MonoBehaviour
{
    public const string RootName = "Birch Meadows Connection";
    public const string RoadName = "Birch Link";
    public static readonly Vector3 GatePosition = new Vector3(206f, 0f, 198f);
    public static readonly Rect EastRoad = Rect.MinMaxRect(155f, 193f, 245f, 203f);
    public static readonly Rect WestRoad = Rect.MinMaxRect(155f, 170f, 165f, 203f);
    public static readonly Rect SouthRoad = Rect.MinMaxRect(235f, 150f, 245f, 203f);
    public const float GateHalfWidth = 8.4f;
    public DistrictGate Gate { get; private set; }
    public int GroundPieces { get; private set; }
    public int ClearedProps { get; private set; }
    public int OpenedFenceColliders { get; private set; }
    static readonly Rect[] Roads = { EastRoad, WestRoad, SouthRoad };

    /// <summary>No scene-name dependency; only the real authored town with its original Meadows entrance qualifies.</summary>
    public static NeighborhoodConnection Ensure(Scene scene)
    {
        if (!scene.IsValid() || !scene.isLoaded) return null;
        Transform town = null;
        foreach (var root in scene.GetRootGameObjects()) if (root.name == "NeighborhoodGen") { town = root.transform; break; }
        return Ensure(town);
    }

    public static NeighborhoodConnection Ensure(Transform town)
    {
        if (town == null || CoastalCityLayout.IsCoastal(town)) return null;
        var existing = town.GetComponentInChildren<NeighborhoodConnection>(true);
        if (existing != null) return existing;
        var ground = town.Find("Ground");
        DistrictGate original = null;
        foreach (var gate in town.GetComponentsInChildren<DistrictGate>(true))
            if (gate.districtId == Districts.MinivanMeadows && (gate.transform.position - new Vector3(206f, 0, -6f)).sqrMagnitude < 1f)
                { original = gate; break; }
        if (ground == null || original == null) return null;

        var root = new GameObject(RootName);
        root.transform.SetParent(town, false);
        var link = root.AddComponent<NeighborhoodConnection>();
        link.BuildGround(ground);
        link.OpenFence(original.transform.parent);
        link.ClearProps(town);
        link.Gate = Instantiate(original.gameObject, link.transform).GetComponent<DistrictGate>();
        link.Gate.name = "Gate_minivan_meadows_north";
        link.Gate.transform.SetPositionAndRotation(GatePosition, Quaternion.Euler(0, 90, 0));
        link.Gate.secondaryEntrance = true;
        link.Gate.SyncState(true);
        link.Wayfinding();
        Physics.SyncTransforms();
        return link;
    }

    public static Rect Expand(Rect rect, float by) => Rect.MinMaxRect(rect.xMin - by, rect.yMin - by, rect.xMax + by, rect.yMax + by);
    static bool InRoad(Vector2 point, float expand = 0f)
    {
        foreach (var road in Roads) if (Expand(road, expand).Contains(point)) return true;
        return false;
    }
    public static bool InCorridor(Vector3 point) => InRoad(new Vector2(point.x, point.z), 3.35f);
    static Rect XZ(Bounds b) => Rect.MinMaxRect(b.min.x, b.min.z, b.max.x, b.max.z);
    static bool TouchesCorridor(Rect rect)
    {
        foreach (var road in Roads) if (Expand(road, 3f).Overlaps(rect)) return true;
        return false;
    }

    /// <summary>Exact rectangle subdivision, keeping every square metre outside the new footprint intact.</summary>
    public static List<Rect> SplitGround(Rect original)
    {
        var xs = new SortedSet<float> { original.xMin, original.xMax };
        var zs = new SortedSet<float> { original.yMin, original.yMax };
        foreach (var road in Roads) foreach (float margin in new[] { 0f, .3f, 3f })
        {
            var cut = Expand(road, margin);
            if (cut.xMin > original.xMin && cut.xMin < original.xMax) xs.Add(cut.xMin);
            if (cut.xMax > original.xMin && cut.xMax < original.xMax) xs.Add(cut.xMax);
            if (cut.yMin > original.yMin && cut.yMin < original.yMax) zs.Add(cut.yMin);
            if (cut.yMax > original.yMin && cut.yMax < original.yMax) zs.Add(cut.yMax);
        }
        var x = new List<float>(xs); var z = new List<float>(zs); var result = new List<Rect>();
        for (int i = 0; i + 1 < x.Count; i++) for (int j = 0; j + 1 < z.Count; j++)
            if (x[i + 1] - x[i] > .001f && z[j + 1] - z[j] > .001f)
                result.Add(Rect.MinMaxRect(x[i], z[j], x[i + 1], z[j + 1]));
        return result;
    }

    void BuildGround(Transform ground)
    {
        Material asphalt = null, concrete = null, curb = null;
        foreach (Transform child in ground)
        {
            var renderer = child.GetComponent<Renderer>();
            if (renderer == null) continue;
            if (child.name.StartsWith("Road_")) asphalt ??= renderer.sharedMaterial;
            if (child.name.StartsWith("Sidewalk_")) concrete ??= renderer.sharedMaterial;
            if (child.name.StartsWith("Curb_")) curb ??= renderer.sharedMaterial;
        }
        foreach (Transform child in ground)
        {
            bool oldRoad = child.name.StartsWith("Road_");
            if (!oldRoad && !child.name.StartsWith("Grass_") && !child.name.StartsWith("Sidewalk_") && !child.name.StartsWith("Curb_")) continue;
            var collider = child.GetComponent<BoxCollider>(); var renderer = child.GetComponent<Renderer>();
            if (collider == null || renderer == null) continue;
            var bounds = TownMapData.WorldBounds(collider);
            var rect = XZ(bounds);
            if (!TouchesCorridor(rect)) continue;
            foreach (var piece in SplitGround(rect))
            {
                float top = bounds.max.y;
                var material = renderer.sharedMaterial;
                if (InRoad(piece.center)) { top = 0f; material = asphalt ?? material; }
                else if (!oldRoad && InRoad(piece.center, .3f)) { top = .15f; material = curb ?? material; }
                else if (!oldRoad && InRoad(piece.center, 3f)) { top = .15f; material = concrete ?? material; }
                Box("Link ground", new Vector3(piece.center.x, (bounds.min.y + top) * .5f, piece.center.y),
                    new Vector3(piece.width, top - bounds.min.y, piece.height), material, 6, true);
                GroundPieces++;
            }
            child.gameObject.SetActive(false);
        }
        var paint = SortingKit.RuntimeMaterial("mat/birch_link_markings", new Color(1f, .82f, .22f));
        for (float x = 170f; x < 230f; x += 6f)
        {
            if (Mathf.Abs(x - GatePosition.x) < 4f) continue;
            Box("Birch Link centre dash", new Vector3(x, .009f, 198f), new Vector3(3f, .014f, .2f), paint, 0, false);
        }
        for (float z = 178f; z < 190f; z += 6f)
            Box("Birch Link west dash", new Vector3(160f, .009f, z), new Vector3(.2f, .014f, 3f), paint, 0, false);
        for (float z = 162f; z < 190f; z += 6f)
            Box("Birch Link east dash", new Vector3(240f, .009f, z), new Vector3(.2f, .014f, 3f), paint, 0, false);
    }

    /// <summary>Only the Meadows perimeter at x=206 is cut; private yards and their knockable fences are excluded.</summary>
    void OpenFence(Transform district)
    {
        if (district == null) return;
        float low = GatePosition.z - GateHalfWidth, high = GatePosition.z + GateHalfWidth;
        foreach (Transform wall in district)
        {
            if (wall.name != "Wall") continue;
            foreach (var collider in wall.GetComponentsInChildren<BoxCollider>(true))
            {
                Bounds b = TownMapData.WorldBounds(collider);
                if (Mathf.Abs(b.center.x - GatePosition.x) > .5f || b.size.x > 1f || b.max.z <= low || b.min.z >= high) continue;
                collider.enabled = false;
                OpenedFenceColliders++;
                ColliderRemainder(b, b.min.z, Mathf.Min(low, b.max.z));
                ColliderRemainder(b, Mathf.Max(high, b.min.z), b.max.z);
            }
            foreach (var renderer in wall.GetComponentsInChildren<MeshRenderer>(true))
            {
                Bounds b = renderer.bounds;
                if (renderer.name != "Panel" || Mathf.Abs(b.center.x - GatePosition.x) > .5f || b.max.z <= low || b.min.z >= high) continue;
                renderer.enabled = false;
                FenceRemainder(b.min.z, Mathf.Min(low, b.max.z), renderer.sharedMaterial);
                FenceRemainder(Mathf.Max(high, b.min.z), b.max.z, renderer.sharedMaterial);
            }
        }
    }

    void ColliderRemainder(Bounds b, float from, float to)
    {
        if (to - from < .001f) return;
        var go = new GameObject("Retained Meadows perimeter"); go.layer = 7;
        go.transform.SetParent(transform, false);
        go.transform.position = new Vector3(b.center.x, b.center.y, (from + to) * .5f);
        go.AddComponent<BoxCollider>().size = new Vector3(b.size.x, b.size.y, to - from);
    }

    void FenceRemainder(float from, float to, Material material)
    {
        if (to - from < .02f) return;
        foreach (float y in new[] { .3f, .78f })
            Box("Retained fence rail", new Vector3(206f, y, (from + to) * .5f), new Vector3(.1f, .12f, to - from), material, 0, false);
        for (float z = from + .1f; z < to; z += .4f)
            Box("Retained fence picket", new Vector3(206f, .55f, z), new Vector3(.1f, 1.1f, Mathf.Min(.16f, to - z)), material, 0, false);
    }

    void ClearProps(Transform town)
    {
        void Check(Transform prop)
        {
            if (!InCorridor(prop.position)) return;
            // Only known decorative groups are visited; houses, lots, trash, players and cars never enter this list.
            prop.gameObject.SetActive(false); ClearedProps++;
        }
        var nature = town.Find("Nature");
        if (nature != null) foreach (Transform prop in nature) Check(prop);
        var streets = new List<Transform>();
        var starter = town.Find("StreetFurniture"); if (starter != null) streets.Add(starter);
        var districts = town.Find("Districts");
        if (districts != null) foreach (Transform district in districts)
        {
            var street = district.Find("StreetFurniture"); if (street != null) streets.Add(street);
            var props = district.Find("Props");
            if (props != null) foreach (Transform prop in props) Check(prop);
        }
        foreach (var street in streets) foreach (Transform frontage in street)
            foreach (Transform prop in frontage) Check(prop);
    }

    void Wayfinding()
    {
        Sign(new Vector3(168.5f, 0, 181f), 0f, "BIRCH LINK\nMINIVAN MEADOWS AHEAD");
        Sign(new Vector3(232f, 0, 153f), 0f, "BIRCH LINK\n< MILDLY PLEASANT");
        Sign(new Vector3(216f, 0, 206.5f), 270f, "MILDLY PLEASANT\nBIRCH ST / DEPOT ROW");
    }

    void Sign(Vector3 at, float yaw, string text)
    {
        var green = SortingKit.RuntimeMaterial("mat/birch_link_sign", new Color(.1f, .29f, .24f));
        var post = Box("Birch Link sign post", at + Vector3.up * 1.45f, new Vector3(.14f, 2.9f, .14f), green, 0, false);
        var board = Box("Birch Link directions", at + Vector3.up * 2.8f, new Vector3(5f, 1.4f, .12f), green, 0, false);
        board.rotation = Quaternion.Euler(0f, yaw, 0f);
        var label = WorkshopModels.MakeLabel(board, "Directions", text, 3f, Color.white);
        // The board carries dimensions in scale; labels need world-sized text, not that non-uniform scale.
        label.transform.SetParent(transform, false);
        label.transform.SetPositionAndRotation(board.position - board.forward * .07f, board.rotation);
        label.rectTransform.sizeDelta = new Vector2(4.7f, 1.2f);
        label.enableAutoSizing = true; label.fontSizeMin = 1f; label.fontSizeMax = 3f;
    }

    Transform Box(string name, Vector3 center, Vector3 size, Material material, int layer, bool collider)
    {
        var go = new GameObject(name) { layer = layer };
        go.transform.SetParent(transform, false);
        go.transform.position = center; go.transform.localScale = size;
        go.AddComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
        go.AddComponent<MeshRenderer>().sharedMaterial = material;
        if (collider) go.AddComponent<BoxCollider>();
        return go.transform;
    }
}
