using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Standard, connected workshop wing. The original truck/sorting/office/spawn coordinates never move.
/// Only the authored east wall and the one east pillar are replaced; no save, scene asset or mesh asset is edited.
/// The full wing stays inside DepotRowLayout's existing pocket bounds, including in legacy solo ShopScene.
/// </summary>
public sealed class WorkshopAnnex : MonoBehaviour
{
    public const string RootName = "Workshop Annex";
    public const float FloorY = -.066f, West = 6.76f, East = 13f, South = -4.5f, North = 3.5f;
    public const float DoorSouth = -1.5f, DoorNorth = 1.2f, DoorHeight = 2.95f;
    public bool WallOpened { get; private set; }
    public int lot = -1;
    public static Bounds LocalBounds => new Bounds(new Vector3((West + East) * .5f, 2f, (South + North) * .5f), new Vector3(East - West, 4.2f, North - South));
    public static Bounds BoundsForLot(int lot) => new Bounds(Origin(lot) + LocalBounds.center, LocalBounds.size);
    public static Vector3 Origin(int lot) => lot >= 0 ? DepotRowLayout.InteriorOrigin(lot) : Vector3.zero;

    /// <summary>scope is the pocket Interior; null means the authored roots of the legacy ShopScene.</summary>
    public static WorkshopAnnex Ensure(Transform scope, Scene scene, int lot)
    {
        if (!scene.IsValid() || !scene.isLoaded) return null;
        var roots = scope != null ? new[] { scope.gameObject } : scene.GetRootGameObjects();
        foreach (var r in roots)
            foreach (var a in r.GetComponentsInChildren<WorkshopAnnex>(true)) if (a.lot == lot) return a;

        var go = new GameObject(RootName);
        if (scope != null) go.transform.SetParent(scope, false);
        else { SceneManager.MoveGameObjectToScene(go, scene); go.transform.position = Origin(lot); }
        var annex = go.AddComponent<WorkshopAnnex>(); annex.lot = lot;
        annex.WallOpened = annex.OpenEastWall(roots);
        if (!annex.WallOpened)
        {
            Debug.LogWarning("[Workshop] Couldn't identify the authored east wall. The annex was not built; existing geometry was kept.");
            if (Application.isPlaying) Object.Destroy(go); else Object.DestroyImmediate(go);
            return null;
        }
        annex.BuildRoom();
        if (scope != null)
        {
            var depot = scope.GetComponentInParent<DepotLot>(true);
            annex.AddRestFloor(depot);
        }
        return annex;
    }

    bool OpenEastWall(GameObject[] roots)
    {
        Vector3 origin = Origin(lot);
        var walls = new List<Renderer>();
        foreach (var r in roots) foreach (var renderer in r.GetComponentsInChildren<Renderer>(true))
        {
            if (renderer == null || renderer.GetComponentInParent<WorkshopAnnex>(true) != null) continue;
            // The real shell is Structure/Mask/wall: a vertical plane, not the office, floor, roof or props.
            var t = renderer.transform;
            if (t.name != "wall" || t.parent == null || t.parent.name != "Mask") continue;
            Bounds b = renderer.bounds; b.center -= origin;
            if (IsEastWall(b)) walls.Add(renderer);
        }
        if (walls.Count == 0) return false;
        Material wallMaterial = walls[0].sharedMaterial;
        foreach (var wall in walls)
        {
            Bounds b = wall.bounds; b.center -= origin;
            wall.enabled = false;
            foreach (var collider in wall.GetComponents<Collider>()) collider.enabled = false;
            // Original plane is ten metres high. Retain its exact extents above/beside the new portal.
            float bottom = Mathf.Min(FloorY, b.min.y), top = b.max.y;
            ShellBox("Original east wall south", new Vector3(West, (bottom + top) * .5f, (b.min.z + DoorSouth) * .5f),
                new Vector3(.14f, top - bottom, Mathf.Max(.01f, DoorSouth - b.min.z)), wallMaterial);
            ShellBox("Original east wall north", new Vector3(West, (bottom + top) * .5f, (DoorNorth + b.max.z) * .5f),
                new Vector3(.14f, top - bottom, Mathf.Max(.01f, b.max.z - DoorNorth)), wallMaterial);
            ShellBox("Original east wall above opening", new Vector3(West, (FloorY + DoorHeight + top) * .5f, (DoorSouth + DoorNorth) * .5f),
                new Vector3(.14f, top - FloorY - DoorHeight, DoorNorth - DoorSouth), wallMaterial);
        }
        foreach (var r in roots) foreach (var t in r.GetComponentsInChildren<Transform>(true))
        {
            if (t.name != "SM_pillar_generic_01 (3)" || t.parent == null || t.parent.name != "Structure") continue;
            var renderer = t.GetComponentInChildren<Renderer>(true); if (renderer == null) continue;
            var b = renderer.bounds; b.center -= origin;
            if (Mathf.Abs(b.center.x - 6.72f) > .15f || Mathf.Abs(b.center.z + .43f) > .15f) continue;
            // Only this identified support overlaps the portal. Matching new jambs/lintel replace it.
            t.gameObject.SetActive(false);
        }
        return true;
    }

    public static bool IsEastWall(Bounds b) => Mathf.Abs(b.center.x - West) < .12f && b.size.x < .35f &&
        b.min.z < DoorSouth && b.max.z > DoorNorth && b.size.y > 5f;

    void BuildRoom()
    {
        var floor = SortingKit.RuntimeMaterial("mat/workshop_floor", new Color(.40f, .43f, .42f));
        var wall = SortingKit.RuntimeMaterial("mat/workshop_wall", new Color(.70f, .73f, .66f));
        var steel = SortingKit.RuntimeMaterial("mat/workshop_structure", new Color(.17f, .26f, .25f));
        var yellow = SortingKit.RuntimeMaterial("mat/workshop_wayfinding", new Color(.90f, .64f, .22f));
        var light = SortingKit.RuntimeMaterial("mat/workshop_light", new Color(.95f, .95f, .80f), true);
        float w = East - West, d = North - South, cx = (West + East) * .5f, cz = (South + North) * .5f;
        ShellBox("Annex floor", new Vector3(cx, FloorY - .09f, cz), new Vector3(w + .15f, .18f, d + .15f), floor,
            layer: SortingKit.GroundLayer);
        ShellBox("East wall", new Vector3(East + .06f, FloorY + 2.15f, cz), new Vector3(.12f, 4.3f, d + .2f), wall);
        ShellBox("North wall", new Vector3(cx, FloorY + 2.15f, North + .06f), new Vector3(w, 4.3f, .12f), wall);
        ShellBox("South wall", new Vector3(cx, FloorY + 2.15f, South - .06f), new Vector3(w, 4.3f, .12f), wall);
        ShellBox("Annex roof", new Vector3(cx, FloorY + 4.36f, cz), new Vector3(w + .24f, .12f, d + .24f), wall);
        foreach (float z in new[] { DoorSouth - .075f, DoorNorth + .075f })
            ShellBox("Portal jamb", new Vector3(West, FloorY + DoorHeight * .5f, z), new Vector3(.28f, DoorHeight, .15f), steel);
        ShellBox("Portal lintel", new Vector3(West, FloorY + DoorHeight + .11f, (DoorSouth + DoorNorth) * .5f), new Vector3(.28f, .22f, DoorNorth - DoorSouth + .3f), steel);
        foreach (float z in new[] { South + .08f, North - .08f })
            ShellBox("Skirting", new Vector3(cx, FloorY + .12f, z), new Vector3(w, .24f, .045f), steel);
        foreach (float z in new[] { -3.2f, -.5f, 2.2f })
        {
            ShellBox("Roof beam", new Vector3(cx, FloorY + 4.11f, z), new Vector3(w, .16f, .11f), steel);
            ShellBox("Workshop strip light", new Vector3(cx, FloorY + 3.99f, z), new Vector3(2.4f, .045f, .14f), light, false);
            // Emissive strips alone do not illuminate runtime-built geometry. Three bounded,
            // shadowless lights fill this wing only and turn off with its hidden depot interior.
            var fixture = new GameObject("Workshop fill light");
            fixture.transform.SetParent(transform, false);
            fixture.transform.localPosition = new Vector3(cx, FloorY + 3.77f, z);
            var fill = fixture.AddComponent<Light>();
            fill.type = LightType.Point;
#if UNITY_EDITOR
            // Bake settings are editor-only; newly created player lights are realtime already.
            fill.lightmapBakeType = LightmapBakeType.Realtime;
#endif
            fill.color = new Color(1f, .97f, .90f);
            fill.range = 5.5f;
            fill.intensity = 6f;
            fill.shadows = LightShadows.None;
        }
        // Flush paint only; no threshold collider or lip in the route between rooms.
        foreach (float z in new[] { DoorSouth + .13f, DoorNorth - .13f })
            ShellBox("Portal floor stripe", new Vector3(West + .42f, FloorY + .003f, z), new Vector3(.98f, .005f, .055f), yellow, false);
        var sign = WorkshopModels.MakeLabel(transform, "Workshop portal sign", "WORKSHOP  /  SHOWROOM", 3f, new Color(.97f, .83f, .41f));
        sign.transform.localPosition = new Vector3(West - .16f, FloorY + DoorHeight + .10f, -.15f);
        sign.transform.localRotation = Quaternion.Euler(0, 90f, 0);
        sign.rectTransform.sizeDelta = new Vector2(2.34f, .24f); sign.enableAutoSizing = true; sign.fontSizeMin = 1f; sign.fontSizeMax = 3f;
    }

    GameObject ShellBox(string name, Vector3 at, Vector3 size, Material material, bool solid = true,
        int layer = SortingKit.WallsLayer)
    {
        // Player and truck cameras avoid Walls, not Ground. Keep vertical shell/roof/portal
        // on that layer; only the walking floor belongs to Ground for floor-finding queries.
        var go = new GameObject(name) { layer = layer };
        go.transform.SetParent(transform, false); go.transform.localPosition = at; go.transform.localScale = size;
        go.AddComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
        go.AddComponent<MeshRenderer>().sharedMaterial = material;
        if (solid) go.AddComponent<BoxCollider>();
        return go;
    }

    void AddRestFloor(DepotLot depot)
    {
        // The original pocket floor ends at x=9.5. Support annex trash even when an offline owner's interior is hidden.
        if (depot == null || depot.pocketFloor == null || depot.pocketFloor.transform.Find("Annex rest floor") != null) return;
        var go = new GameObject("Annex rest floor") { layer = SortingKit.GroundLayer };
        go.transform.SetParent(depot.pocketFloor.transform, false);
        go.transform.position = Origin(lot) + new Vector3((9.4f + East) * .5f, FloorY - .09f, (South + North) * .5f);
        var box = go.AddComponent<BoxCollider>(); box.size = new Vector3(East - 9.4f + .15f, .18f, North - South + .15f);
    }
}
