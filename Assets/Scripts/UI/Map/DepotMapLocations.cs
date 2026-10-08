using UnityEngine;

/// <summary>Stable lot identities on the town map; never mistake the first garage POI for every guest's business.</summary>
public static class DepotMapLocations
{
    const string Prefix = "depot_lot_";

    public static string PoiId(int lot) => Prefix + lot;

    public static int LotFromId(string id)
    {
        if (id == null || !id.StartsWith(Prefix, System.StringComparison.Ordinal)) return -1;
        return int.TryParse(id.Substring(Prefix.Length), out int lot) && lot >= 0 && lot < DepotRowLayout.LotCount ? lot : -1;
    }

    /// <summary>Inside a coworker's shop, "you are here" means that shop, not the visitor's own business.</summary>
    public static int SelectLot(int ownedLot, int interiorLot) =>
        Valid(interiorLot) ? interiorLot : Valid(ownedLot) ? ownedLot : -1;

    public static int LocalLot => DepotRowWorld.Active
        ? SelectLot(DepotRowWorld.LocalLot, DepotRowWorld.LocalInterior) : -1;

    static bool Valid(int lot) => lot >= 0 && lot < DepotRowLayout.LotCount;

    public static bool HasLotPois(TownMapData data)
    {
        if (data != null)
            foreach (var p in data.pois)
                if (p.kind == MapPoiKind.GarageDoor && LotFromId(p.id) >= 0) return true;
        return false;
    }

    public static bool TryLocalPosition(TownMapData data, out Vector2 position) => TryPosition(data, LocalLot, out position);

    public static bool TryPosition(TownMapData data, int lot, out Vector2 position)
    {
        // Outside a session (map previews / solo), canonical lot 0 is the default depot. Prefer it over an old
        // municipal garage POI even if that legacy POI happened to be baked first.
        int wanted = Valid(lot) ? lot : 0;
        if (data != null)
            foreach (var p in data.pois)
                if (p.kind == MapPoiKind.GarageDoor && LotFromId(p.id) == wanted)
                { position = p.pos; return true; }
        if (Valid(lot) || HasLotPois(data))
        {
            var door = DepotRowLayout.DoorPoint(wanted);
            position = new Vector2(door.x, door.z);
            return true;
        }
        if (data != null)
            foreach (var p in data.pois)
                if (p.kind == MapPoiKind.GarageDoor) { position = p.pos; return true; }
        position = Vector2.zero;
        return false;
    }
}
