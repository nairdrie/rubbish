using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// Identifies a generated road group and the blueprint version used to build it. The incremental editor pass compares
/// this fingerprint with RoadDef.Fingerprint; this marker performs no automatic regeneration or runtime movement.
/// </summary>
[DisallowMultipleComponent]
public sealed class CityGeneratedRoad : MonoBehaviour
{
    public const int SurfaceRevision = 2;
    public string roadId = "";
    public string fingerprint = "";
    public string surfaceFingerprint = "";

    // A branch changes the neighbouring road's sidewalk opening too. Track the complete surface network
    // separately so a mesh-only refresh does not recreate unchanged lamps or their saved knockable IDs.
    public static string SurfaceFingerprint(IReadOnlyList<CoastalCityLayout.RoadDef> roads)
    {
        var source = new System.Text.StringBuilder("joined-road-surfaces/").Append(SurfaceRevision);
        foreach (var road in roads.OrderBy(r => r.id, StringComparer.Ordinal)) source.Append('|').Append(road.Fingerprint);
        return Hash128.Compute(source.ToString()).ToString();
    }
}
