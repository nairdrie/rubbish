using System.Collections.Generic;
using UnityEngine;

/// <summary>Generated town plus the explicitly hand-authored layer in the same scene. Never visits another
/// loaded town, prefab asset, inactive legacy neighbourhood or depot scene.</summary>
public static class TownAuthoringScope
{
    public const string RootName = "TownAuthored";

    public static Transform AuthoredRoot(Transform town)
    {
        if (town == null || !town.gameObject.scene.IsValid() || !town.gameObject.scene.isLoaded) return null;
        foreach (var root in town.gameObject.scene.GetRootGameObjects())
            if (root.name == RootName && root.transform != town) return root.transform;
        return null;
    }

    public static T[] Components<T>(Transform town) where T : Component
    {
        if (town == null) return System.Array.Empty<T>();
        var result = new List<T>(town.GetComponentsInChildren<T>(true));
        var authored = AuthoredRoot(town);
        if (authored != null && !authored.IsChildOf(town)) result.AddRange(authored.GetComponentsInChildren<T>(true));
        return result.ToArray();
    }

    public static bool Contains(Transform town, Transform item)
    {
        if (town == null || item == null) return false;
        if (item == town || item.IsChildOf(town)) return true;
        var authored = AuthoredRoot(town);
        return authored != null && (item == authored || item.IsChildOf(authored));
    }
}
