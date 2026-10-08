using System.Collections.Generic;
using UnityEngine;

/// <summary>Park entrances shared by world markings, navigation and crossing guards. Derived from the blueprint.</summary>
public static class CoastalParkRoutes
{
    public struct Crossing
    {
        public string roadId;
        public Vector3 position, forward;
        public float halfWidth;
    }
    public static Crossing[] Crossings()
    {
        var park = CoastalCityLayout.Landmark("pickle-park");
        var candidates = new List<Crossing>();
        foreach (var road in CoastalCityLayout.Roads)
        {
            if (road.districtId != Districts.MildlyPleasant || road.bridge || road.service) continue;
            float best = float.MaxValue; var choice = new Crossing { roadId = road.id, halfWidth = road.halfWidth };
            for (int i = 1; i < road.points.Length; i++)
            {
                var a = road.points[i - 1]; var delta = road.points[i] - a;
                var p = a + delta * Mathf.Clamp01(Vector3.Dot(park - a, delta) / Mathf.Max(.0001f, delta.sqrMagnitude));
                float distance = (p - park).sqrMagnitude;
                if (distance >= best) continue;
                best = distance; choice.position = p; choice.forward = delta.normalized;
            }
            candidates.Add(choice);
        }
        candidates.Sort((a, b) =>
        {
            int distance = (a.position - park).sqrMagnitude.CompareTo((b.position - park).sqrMagnitude);
            return distance != 0 ? distance : string.CompareOrdinal(a.roadId, b.roadId);
        });
        var result = new List<Crossing>(2);
        foreach (var candidate in candidates)
        {
            bool nearby = false;
            foreach (var previous in result) if ((previous.position - candidate.position).sqrMagnitude < 20 * 20) nearby = true;
            if (nearby) continue;
            result.Add(candidate); if (result.Count == 2) break;
        }
        return result.ToArray();
    }
}
