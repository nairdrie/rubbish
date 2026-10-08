using System;
using System.Linq;
using Newtonsoft.Json;
using UnityEngine;

/// <summary>Prevents road-only publishing from silently leaving broader layout edits half-applied.</summary>
[DisallowMultipleComponent]
public sealed class CityBlueprintStamp : MonoBehaviour
{
    public string structuralFingerprint;

    public static string StructuralFingerprint(CoastalCityLayout.Blueprint data)
    {
        var terrainIds = data.terrain.river.Concat(data.terrain.coast).Concat(data.terrain.riverWalk).ToArray();
        var payload = new {
            data.schema, data.scale, data.bounds, data.terrain, data.landmarks, data.lotStrips, data.collectionSites, data.infillSites,
            terrainNodes = data.nodes.Where(n => Array.IndexOf(terrainIds, n.id) >= 0).OrderBy(n => n.id, StringComparer.Ordinal).ToArray()
        };
        return Hash128.Compute(JsonConvert.SerializeObject(payload)).ToString();
    }

    public bool Matches(CoastalCityLayout.Blueprint data) => structuralFingerprint == StructuralFingerprint(data);
}
