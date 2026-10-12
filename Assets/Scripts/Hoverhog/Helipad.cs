using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// The rooftop helipad that comes with the Hoverhog (Docs/Progression.md §6.10): the pad, the roof hatch and the
/// stairwell hut on the depot's flat roof, plus the stairwell's other end inside the shop. Built at runtime on every
/// machine from the replicated upgrade level, one per lot (the walk-up hatch's pattern): nothing is authored into a scene.
/// </summary>
public class Helipad : MonoBehaviour
{
    static readonly Helipad[] byLot = new Helipad[DepotRowLayout.LotCount];
    static readonly Dictionary<TippingFloor, GameObject> shopStairs = new Dictionary<TippingFloor, GameObject>();
    static readonly List<TippingFloor> gone = new List<TippingFloor>();

    int lot;

    /// <summary>Every machine, about twice a second.</summary>
    public static void Sync()
    {
        for (int i = 0; i < byLot.Length; i++)
        {
            var depot = DepotRowScene.Lot(i);
            bool want = depot != null && depot.facade != null && depot.facade.gameObject.activeInHierarchy && Hogs.Level(i) > 0;
            var pad = byLot[i];
            if (pad == null)
            {
                if (!want) continue;
                pad = byLot[i] = Build(i, depot);
                Vector3 at = DepotRowLayout.StreetPoint(i, HogTuning.PadLocal);
                Fx.Dust(at, 2f);
                Sfx.Play("slam", at, 0.7f, 0.9f);
            }
            if (pad.gameObject.activeSelf != want) pad.gameObject.SetActive(want);
        }
        SyncShopStairs();
        SyncSatPads();
    }

    void OnDestroy() { if (lot >= 0 && lot < byLot.Length && byLot[lot] == this) byLot[lot] = null; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        for (int i = 0; i < byLot.Length; i++) byLot[i] = null;
        shopStairs.Clear();
        for (int i = 0; i < satPads.Length; i++) satPads[i] = null;
    }

    // ------------------------------------------------------------------ the roof

    static Helipad Build(int lot, DepotLot depot)
    {
        var go = new GameObject($"Helipad (lot {lot})") { layer = 2 };
        go.transform.SetParent(depot.transform, true);
        var pose = DepotRowLayout.StreetPose(lot, new Vector3(0f, HogTuning.RoofY, 0f));
        go.transform.SetPositionAndRotation(pose.position, pose.rotation);
        var pad = go.AddComponent<Helipad>();
        pad.lot = lot;
        Transform root = go.transform;

        var yellow = ShopModKit.Lit("mat/hog_hazard", new Color(1f, 0.85f, 0.1f), 0.4f);
        var white = ShopModKit.Lit("mat/shop_white", new Color(0.97f, 0.97f, 0.97f), 0.6f);
        var steel = ShopModKit.Lit("mat/hog_belly_steel", new Color(0.36f, 0.4f, 0.45f), 0.6f, 0.7f);
        var black = ShopModKit.Lit("mat/shop_headlamp_body", new Color(0.16f, 0.16f, 0.2f), 0.4f);
        var orange = ShopModKit.Lit("mat/shop_orange", new Color(1f, 0.6f, 0f), 0.4f);
        var brick = ShopModKit.Lit("mat/hog_hut_brick", new Color(0.62f, 0.36f, 0.3f), 0.15f);
        var lamp = ShopModKit.Emissive("mat/hog_lamp_green", new Color(0.3f, 1f, 0.35f), 2.5f);
        var bolt = ShopModKit.Emissive("mat/hog_lamp_amber", new Color(1f, 0.7f, 0.1f), 2.5f);

        // One flat deck over the facade's stepped roof: everything up here stands on it
        var deckMat = ShopModKit.Lit("mat/hog_roof_deck", new Color(0.55f, 0.56f, 0.58f), 0.15f);
        ShopModKit.Prim(PrimitiveType.Cube, "Roof deck", root, deckMat, HogTuning.DeckCentre, HogTuning.DeckSize);

        Vector3 p = new Vector3(HogTuning.PadLocal.x, 0f, HogTuning.PadLocal.z);
        Vector3 puck = BuildPad(root, p);
        // Windsock on the back corner
        Vector3 sock = new Vector3(-9f, 0f, -7.5f);
        ShopModKit.Prim(PrimitiveType.Cylinder, "Windsock pole", root, steel, sock + Vector3.up * 1.4f, new Vector3(0.09f, 1.4f, 0.09f));
        ShopModKit.Prim(PrimitiveType.Cylinder, "Windsock", root, orange, sock + new Vector3(0.55f, 2.65f, 0f), new Vector3(0.36f, 0.5f, 0.36f), new Vector3(0f, 0f, 80f));
        ShopModKit.Prim(PrimitiveType.Cylinder, "Windsock band", root, white, sock + new Vector3(0.7f, 2.62f, 0f), new Vector3(0.38f, 0.1f, 0.38f), new Vector3(0f, 0f, 80f));

        // The roof hatch: where a hovering Hoverhog's load goes down into the shop
        Vector3 h = new Vector3(HogTuning.HatchLocal.x, 0f, HogTuning.HatchLocal.z);
        ShopModKit.Prim(PrimitiveType.Cube, "Hatch frame", root, steel, h + Vector3.up * 0.06f, new Vector3(2.1f, 0.12f, 2.1f));
        for (int s = -1; s <= 1; s += 2)
            ShopModKit.Prim(PrimitiveType.Cube, "Hatch door", root, s < 0 ? yellow : black, h + new Vector3(s * 0.46f, 0.13f, 0f), new Vector3(0.9f, 0.04f, 1.8f));
        var hatchLabel = WorkshopModels.MakeLabel(root, "Hatch label", "BAGS IN", 3f, Color.white);
        hatchLabel.transform.localPosition = h + new Vector3(0f, 0.17f, 1.3f);
        hatchLabel.transform.localRotation = Quaternion.Euler(90f, 0f, 0f);

        // The stairwell hut, door facing the pad (−X)
        Vector3 hut = new Vector3(HogTuning.StairHutLocal.x, 0f, HogTuning.StairHutLocal.z);
        ShopModKit.Prim(PrimitiveType.Cube, "Stair hut", root, brick, hut + Vector3.up * 1.25f, new Vector3(2.2f, 2.5f, 2.6f));
        ShopModKit.Prim(PrimitiveType.Cube, "Stair hut roof", root, steel, hut + new Vector3(0.05f, 2.58f, 0f), new Vector3(2.5f, 0.14f, 2.9f), new Vector3(0f, 0f, -6f));
        ShopModKit.Prim(PrimitiveType.Cube, "Stair door", root, black, hut + new Vector3(-1.11f, 1.02f, 0f), new Vector3(0.06f, 2.04f, 1.1f));
        ShopModKit.Prim(PrimitiveType.Sphere, "Stair door knob", root, yellow, hut + new Vector3(-1.17f, 1.0f, 0.38f), Vector3.one * 0.12f);
        var sign = WorkshopModels.MakeLabel(root, "Stair sign", "STAIRS", 2.6f, Color.white);
        sign.transform.localPosition = hut + new Vector3(-1.13f, 2.25f, 0f);
        sign.transform.localRotation = Quaternion.Euler(0f, 90f, 0f);

        foreach (var rend in root.GetComponentsInChildren<Renderer>(true)) rend.gameObject.layer = 2;

        var deck = new GameObject("Roof deck collider") { layer = SortingKit.GroundLayer };
        deck.transform.SetParent(root, false);
        var deckBox = deck.AddComponent<BoxCollider>();
        deckBox.center = HogTuning.DeckCentre;
        deckBox.size = HogTuning.DeckSize;
        var solid = new GameObject("Stair hut collider") { layer = SortingKit.WallsLayer };
        solid.transform.SetParent(root, false);
        var box = solid.AddComponent<BoxCollider>();
        box.center = hut + Vector3.up * 1.3f;
        box.size = new Vector3(2.2f, 2.6f, 2.6f);
        var charger = new GameObject("Charger collider") { layer = SortingKit.WallsLayer };
        charger.transform.SetParent(root, false);
        var cbox = charger.AddComponent<BoxCollider>();
        cbox.center = puck + Vector3.up * 0.35f;
        cbox.size = new Vector3(0.6f, 0.7f, 0.45f);

        AddDoor(root, hut + new Vector3(-1.75f, 1.1f, 0f), new Vector3(1.3f, 2.2f, 1.7f), lot, false);
        return pad;
    }

    /// <summary>The pad itself (ring, deck, H, eight lamps, charger) around <paramref name="p"/> in <paramref name="root"/>'s
    /// space; shared by the roof pad and the satellite pads. Returns where the charger stands.</summary>
    static Vector3 BuildPad(Transform root, Vector3 p)
    {
        var asphalt = ShopModKit.Lit("mat/hog_pad_asphalt", new Color(0.2f, 0.21f, 0.24f), 0.2f);
        var yellow = ShopModKit.Lit("mat/hog_hazard", new Color(1f, 0.85f, 0.1f), 0.4f);
        var white = ShopModKit.Lit("mat/shop_white", new Color(0.97f, 0.97f, 0.97f), 0.6f);
        var steel = ShopModKit.Lit("mat/hog_belly_steel", new Color(0.36f, 0.4f, 0.45f), 0.6f, 0.7f);
        var lamp = ShopModKit.Emissive("mat/hog_lamp_green", new Color(0.3f, 1f, 0.35f), 2.5f);
        var bolt = ShopModKit.Emissive("mat/hog_lamp_amber", new Color(1f, 0.7f, 0.1f), 2.5f);
        float r = HogTuning.PadRadius;
        ShopModKit.Prim(PrimitiveType.Cylinder, "Pad ring", root, yellow, p + Vector3.up * 0.01f, new Vector3(r * 2f + 0.3f, 0.01f, r * 2f + 0.3f));
        ShopModKit.Prim(PrimitiveType.Cylinder, "Pad deck", root, asphalt, p + Vector3.up * 0.02f, new Vector3(r * 2f, 0.012f, r * 2f));
        for (int s = -1; s <= 1; s += 2)
            ShopModKit.Prim(PrimitiveType.Cube, "H bar", root, white, p + new Vector3(s * 0.85f, 0.036f, 0f), new Vector3(0.38f, 0.01f, 2.5f));
        ShopModKit.Prim(PrimitiveType.Cube, "H cross", root, white, p + new Vector3(0f, 0.036f, 0f), new Vector3(1.7f, 0.01f, 0.38f));
        for (int i = 0; i < 8; i++)
        {
            float a = i * Mathf.PI / 4f + 0.39f;
            ShopModKit.Prim(PrimitiveType.Sphere, "Pad lamp", root, lamp, p + new Vector3(Mathf.Cos(a) * (r + 0.05f), 0.08f, Mathf.Sin(a) * (r + 0.05f)), Vector3.one * 0.18f);
        }
        Vector3 puck = p + new Vector3(2.75f, 0f, -2.2f);
        ShopModKit.Prim(PrimitiveType.Cube, "Charger", root, steel, puck + Vector3.up * 0.35f, new Vector3(0.6f, 0.7f, 0.45f));
        ShopModKit.Prim(PrimitiveType.Cube, "Charger bolt", root, bolt, puck + new Vector3(-0.31f, 0.4f, 0f), new Vector3(0.02f, 0.34f, 0.16f), new Vector3(20f, 0f, 0f));
        return puck;
    }

    // ------------------------------------------------------------------ satellite pads (stage 2)

    static readonly GameObject[] satPads = new GameObject[3];

    /// <summary>Is satellite pad <paramref name="index"/> standing? It is once any business in town has bought it.</summary>
    static bool SatPadWanted(int index)
    {
        for (int lot = 0; lot < DepotRowLayout.LotCount; lot++)
            if (Hogs.Level(lot) > 0 && Hogs.LevelsOf(lot).pads > index) return true;
        return false;
    }

    static void SyncSatPads()
    {
        var town = DepotRowScene.Instance;
        for (int i = 0; i < satPads.Length && i < HogTuning.SatPads.Length; i++)
        {
            bool want = town != null && SatPadWanted(i);
            if (satPads[i] == null)
            {
                if (!want) continue;
                satPads[i] = BuildSatPad(i, town.transform);
                Fx.Dust(satPads[i].transform.position, 2f);
            }
            if (satPads[i].activeSelf != want) satPads[i].SetActive(want);
        }
    }

    static GameObject BuildSatPad(int index, Transform parent)
    {
        var spec = HogTuning.SatPads[index];
        Vector3 at = HogTuning.SatPadPosition(index);
        if (spec.pos.y < 0.5f && Physics.Raycast(at + Vector3.up * 30f, Vector3.down, out var hit, 60f, 1 << SortingKit.GroundLayer, QueryTriggerInteraction.Ignore))
            at.y = hit.point.y;
        var go = new GameObject($"Satellite pad ({spec.districtId})") { layer = 2 };
        go.transform.SetParent(parent, true);
        go.transform.SetPositionAndRotation(at, Quaternion.Euler(0f, spec.yaw, 0f));
        Transform root = go.transform;
        var slab = ShopModKit.Lit("mat/hog_roof_deck", new Color(0.55f, 0.56f, 0.58f), 0.15f);
        var steel = ShopModKit.Lit("mat/hog_belly_steel", new Color(0.36f, 0.4f, 0.45f), 0.6f, 0.7f);
        var black = ShopModKit.Lit("mat/shop_headlamp_body", new Color(0.16f, 0.16f, 0.2f), 0.4f);
        var orange = ShopModKit.Lit("mat/shop_orange", new Color(1f, 0.6f, 0f), 0.4f);
        var white = ShopModKit.Lit("mat/shop_white", new Color(0.97f, 0.97f, 0.97f), 0.6f);
        float side = HogTuning.PadRadius * 2f + 1.2f;
        ShopModKit.Prim(PrimitiveType.Cube, "Slab", root, slab, new Vector3(0f, 0.06f, 0f), new Vector3(side, 0.12f, side));
        Vector3 p = new Vector3(0f, 0.12f, 0f);
        Vector3 puck = BuildPad(root, p);
        // A sign post and a windsock, so it can be found from the air
        Vector3 post = new Vector3(-side * 0.5f + 0.3f, 0.12f, -side * 0.5f + 0.3f);
        ShopModKit.Prim(PrimitiveType.Cylinder, "Sign post", root, steel, post + Vector3.up * 1.5f, new Vector3(0.1f, 1.5f, 0.1f));
        ShopModKit.Prim(PrimitiveType.Cube, "Sign board", root, black, post + new Vector3(0.9f, 2.5f, 0f), new Vector3(2.4f, 0.8f, 0.08f));
        foreach (int face in new[] { 1, -1 })
        {
            var sign = WorkshopModels.MakeLabel(root, "Sign", $"{Scrapazon.Name.ToUpperInvariant()} AIR · {spec.label}", 1.5f, Color.white);
            sign.transform.localPosition = post + new Vector3(0.9f, 2.5f, -0.05f * face);
            sign.transform.localRotation = Quaternion.Euler(0f, face > 0 ? 0f : 180f, 0f);
        }
        Vector3 sock = new Vector3(side * 0.5f - 0.3f, 0.12f, -side * 0.5f + 0.3f);
        ShopModKit.Prim(PrimitiveType.Cylinder, "Windsock pole", root, steel, sock + Vector3.up * 1.6f, new Vector3(0.09f, 1.6f, 0.09f));
        ShopModKit.Prim(PrimitiveType.Cylinder, "Windsock", root, orange, sock + new Vector3(0.55f, 3.05f, 0f), new Vector3(0.36f, 0.5f, 0.36f), new Vector3(0f, 0f, 80f));
        ShopModKit.Prim(PrimitiveType.Cylinder, "Windsock band", root, white, sock + new Vector3(0.7f, 3.02f, 0f), new Vector3(0.38f, 0.1f, 0.38f), new Vector3(0f, 0f, 80f));
        foreach (var rend in root.GetComponentsInChildren<Renderer>(true)) rend.gameObject.layer = 2;

        var solid = new GameObject("Slab collider") { layer = SortingKit.GroundLayer };
        solid.transform.SetParent(root, false);
        var box = solid.AddComponent<BoxCollider>();
        box.center = new Vector3(0f, 0.06f, 0f);
        box.size = new Vector3(side, 0.12f, side);
        var charger = new GameObject("Charger collider") { layer = SortingKit.WallsLayer };
        charger.transform.SetParent(root, false);
        var cbox = charger.AddComponent<BoxCollider>();
        cbox.center = puck + Vector3.up * 0.35f;
        cbox.size = new Vector3(0.6f, 0.7f, 0.45f);
        return go;
    }

    static void AddDoor(Transform root, Vector3 localCentre, Vector3 size, int lot, bool toRoof)
    {
        var go = new GameObject(toRoof ? "Stairs up" : "Stairs down") { layer = Carryable.InteractableLayer };
        go.transform.SetParent(root, false);
        go.transform.localPosition = localCentre;
        var trigger = go.AddComponent<BoxCollider>();
        trigger.isTrigger = true;
        trigger.size = size;
        var door = go.AddComponent<HogStairDoor>();
        door.lot = lot;
        door.toRoof = toRoof;
    }

    // ------------------------------------------------------------------ the shop end of the stairwell

    /// <summary>Where ShopScene's (0,0,0) is for this tipping floor: the scene itself in solo, the lot's pocket interior in co-op.</summary>
    public static Vector3 ShopOrigin(TippingFloor floor)
    {
        Vector3 o = floor.pileCenter - HogTuning.ShopPileCentre;
        o.y = floor.FloorY;
        return o;
    }

    static int LotOf(TippingFloor floor)
    {
        int lot = floor.Lot;
        return lot >= 0 ? lot : DepotRowWorld.Active ? -1 : DepotFlowRules.LegacyShopLot;
    }

    static void SyncShopStairs()
    {
        gone.Clear();
        foreach (var kv in shopStairs) if (kv.Key == null || kv.Value == null) gone.Add(kv.Key);
        for (int i = 0; i < gone.Count; i++) shopStairs.Remove(gone[i]);

        var floors = TippingFloor.All;
        for (int i = 0; i < floors.Count; i++)
        {
            var floor = floors[i];
            if (floor == null) continue;
            int lot = LotOf(floor);
            bool want = lot >= 0 && Hogs.Level(lot) > 0;
            if (!shopStairs.TryGetValue(floor, out var stairs) || stairs == null)
            {
                if (!want) continue;
                stairs = shopStairs[floor] = BuildShopStairs(floor, lot);
                Fx.Dust(stairs.transform.position + Vector3.up, 1.4f);
                Sfx.Play("slam", stairs.transform.position, 0.6f, 1.1f);
            }
            if (stairs.activeSelf != want) stairs.SetActive(want);
        }
    }

    static GameObject BuildShopStairs(TippingFloor floor, int lot)
    {
        var go = new GameObject("Helipad stairs") { layer = 2 };
        go.transform.position = ShopOrigin(floor) + HogTuning.ShopStairLocal;
        go.transform.SetParent(floor.transform, true);
        Transform root = go.transform;
        var steel = ShopModKit.Lit("mat/hog_belly_steel", new Color(0.36f, 0.4f, 0.45f), 0.6f, 0.7f);
        var yellow = ShopModKit.Lit("mat/hog_hazard", new Color(1f, 0.85f, 0.1f), 0.4f);
        var black = ShopModKit.Lit("mat/shop_headlamp_body", new Color(0.16f, 0.16f, 0.2f), 0.4f);
        var lamp = ShopModKit.Emissive("mat/hog_lamp_green", new Color(0.3f, 1f, 0.35f), 2.5f);

        // Three steps up into a dark doorway in the south wall (local +Z faces the room)
        for (int i = 0; i < 3; i++)
            ShopModKit.Prim(PrimitiveType.Cube, "Step", root, steel, new Vector3(0f, 0.09f + i * 0.18f, 0.55f - i * 0.3f), new Vector3(1.3f, 0.18f, 0.3f));
        ShopModKit.Prim(PrimitiveType.Cube, "Doorway", root, black, new Vector3(0f, 1.6f, -0.3f), new Vector3(1.15f, 2.1f, 0.1f));
        for (int s = -1; s <= 1; s += 2)
        {
            ShopModKit.Prim(PrimitiveType.Cube, "Door post", root, yellow, new Vector3(s * 0.66f, 1.35f, -0.26f), new Vector3(0.14f, 2.7f, 0.16f));
            ShopModKit.Prim(PrimitiveType.Cube, "Rail", root, yellow, new Vector3(s * 0.66f, 0.95f, 0.2f), new Vector3(0.06f, 0.06f, 1.0f), new Vector3(-31f, 0f, 0f));
        }
        ShopModKit.Prim(PrimitiveType.Cube, "Door lintel", root, yellow, new Vector3(0f, 2.74f, -0.26f), new Vector3(1.46f, 0.14f, 0.16f));
        ShopModKit.Prim(PrimitiveType.Sphere, "Door lamp", root, lamp, new Vector3(0f, 3.52f, -0.24f), Vector3.one * 0.16f);
        var sign = WorkshopModels.MakeLabel(root, "Stairs sign", "TO THE HELIPAD", 2.4f, Color.white);
        sign.transform.localPosition = new Vector3(0f, 3.12f, -0.22f);
        sign.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
        ShopModKit.Prim(PrimitiveType.Cube, "Sign board", root, black, new Vector3(0f, 3.12f, -0.27f), new Vector3(2.3f, 0.5f, 0.06f));
        foreach (var rend in root.GetComponentsInChildren<Renderer>(true)) rend.gameObject.layer = 2;

        AddDoor(root, new Vector3(0f, 1.1f, 0.35f), new Vector3(1.5f, 2.2f, 1.5f), lot, true);
        return go;
    }
}

/// <summary>One end of the helipad stairwell. Like a shop door, it moves only the player who uses it.</summary>
public class HogStairDoor : MonoBehaviour, IInteractable
{
    public int lot;
    public bool toRoof;

    public bool CanInteract()
    {
        var me = PlayerController.Local; // coop:allow(local) a door prompt is for this machine's own player
        return me != null && !me.IsDriving && !DepotRowWorld.Transitioning && !SceneController.IsTransitioning;
    }

    public string GetInteractionPrompt() => toRoof ? "Take the stairs up to the helipad" : "Take the stairs down into the shop";

    public void Highlight(bool highlight) { }

    public void OnInteract(GameObject interactor)
    {
        var pc = interactor != null ? interactor.GetComponent<PlayerController>() : null;
        if (pc == null || !pc.IsOwner || !CanInteract()) return;
        var carrier = interactor.GetComponent<TrashCarrier>();
        if (carrier != null && carrier.IsCarrying)
        {
            GameHUD.Toast("Can't take trash up the stairs! Drop it first (G)", ToastStyle.Bad, 2f);
            Sfx.Play("buzz", pc.transform.position, 0.7f);
            return;
        }
        HogRuntime.UseStairs(lot, toRoof);
    }
}
