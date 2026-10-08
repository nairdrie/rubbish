if (UnityEditor.EditorApplication.isPlaying) throw new System.InvalidOperationException("Edit Mode required.");
var scene = TownPlacementTools.Scene(true);
if (scene.isDirty && (TownPlacementTools.Root() == null || TownPlacementTools.Root().GetComponentsInChildren<TownPlacedAsset>().Any(x => !x.name.StartsWith("QA TEMP "))))
    throw new System.InvalidOperationException("Start the probe with a clean saved town (or its own existing QA draft).");
var palette = TownAssetLibrary.Ensure();
var before = scene.GetRootGameObjects().SelectMany(g => g.GetComponentsInChildren<PickupStop>(true)).Count();
var report = new System.Collections.Generic.List<string>();
foreach (string entryId in new[] { "builtin/SM_house_suburb_001", "builtin/corner-grocer", "builtin/evergreen", "builtin/SM_streetlamp_01" })
{
    var entry = palette.entries.First(e => e.id == entryId);
    UnityEngine.GameObject placed = TownPlacementTools.Root()?.GetComponentsInChildren<TownPlacedAsset>().FirstOrDefault(x => x.paletteId == entryId && x.name.StartsWith("QA TEMP "))?.gameObject;
    var errors = new System.Collections.Generic.Dictionary<string, int>();
    foreach (var road in CoastalCityLayout.Roads.Where(r => !r.bridge))
    {
        if (placed) break;
        for (int i = 1; i < 12 && placed == null; i++) foreach (int side in new[] { 1, -1 })
        {
            var frame = road.AtNormalized(i / 12f);
            var point = frame.position + frame.rotation * UnityEngine.Vector3.right * side * (road.halfWidth + (TownPlacementTools.IsProperty(entry) ? 22 : 34));
            if (!TownPlacementTools.GroundAt(point, out var ground)) continue;
            placed = TownPlacementTools.Place(entry, ground, UnityEngine.Quaternion.identity, 1, false, out var why);
            if (placed) break;
            if (why != null) errors[why] = errors.TryGetValue(why, out var n) ? n + 1 : 1;
        }
        if (placed) break;
    }
    if (placed)
    {
        if (!placed.name.StartsWith("QA TEMP ")) placed.name = "QA TEMP " + placed.name;
        var stop = placed.GetComponentInChildren<PickupStop>(true);
        report.Add(entryId + " OK @" + placed.transform.position + (stop ? " stop=" + stop.stopId + " district=" + stop.districtId + " bags=" + stop.bagCount : ""));
    }
    else report.Add(entryId + " FAILED " + string.Join("; ", errors.Select(e => e.Key + "=" + e.Value)));
}
UnityEditor.Undo.FlushUndoRecordObjects();
var last = UnityEditor.Selection.activeGameObject;
var count = TownPlacementTools.Root().GetComponentsInChildren<TownPlacedAsset>(true).Length;
report.Add("Baseline stops=" + before + " test objects=" + count);
System.IO.Directory.CreateDirectory("Logs/TownAuthoringQA");
System.IO.File.WriteAllText("Logs/TownAuthoringQA/placement.txt", string.Join("\n", report));
return report;
