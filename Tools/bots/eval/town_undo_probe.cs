var scene = TownPlacementTools.Scene();
var root = TownPlacementTools.Root();
var house = root.GetComponentsInChildren<TownPlacedAsset>().First(x => x.name.StartsWith("QA TEMP ") && x.role == TownAssetRole.House);
string identity = house.GetComponentInChildren<PickupStop>().stopId;
var original = house.transform.position;
bool moved = false; string error = null;
foreach (var road in CoastalCityLayout.Roads.Where(r => !r.bridge))
{
    for (int i = 1; i < 12 && !moved; i++) foreach (int side in new[] { 1, -1 })
    {
        var frame = road.AtNormalized(i / 12f);
        var point = frame.position + frame.rotation * UnityEngine.Vector3.right * side * (road.halfWidth + 22);
        if ((point - original).sqrMagnitude < 1600) continue;
        moved = TownGameplayAuthoring.MoveProperty(house, point, out error);
        if (moved) break;
    }
    if (moved) break;
}
if (!moved) throw new System.Exception("Move QA failed: " + error);
if (house.GetComponentInChildren<PickupStop>().stopId != identity) throw new System.Exception("Moving changed identity");
var destination = house.transform.position;
UnityEditor.Undo.FlushUndoRecordObjects(); UnityEditor.Undo.PerformUndo();
if (UnityEngine.Vector3.Distance(house.transform.position, original) > .001f) throw new System.Exception("Move undo failed");
UnityEditor.Undo.PerformRedo();
if (UnityEngine.Vector3.Distance(house.transform.position, destination) > .001f) throw new System.Exception("Move redo failed");
UnityEditor.Undo.PerformUndo();

// The native stroke Undo path restores all objects and stable metadata together.
var before = root.GetComponentsInChildren<TownPlacedAsset>().Length;
UnityEditor.Undo.IncrementCurrentGroup(); int group = UnityEditor.Undo.GetCurrentGroup();
UnityEditor.Undo.SetCurrentGroupName("QA authored layer erase");
int erased = TownPlacementTools.Erase(house.transform.position, 10000, true);
UnityEditor.Undo.CollapseUndoOperations(group); UnityEditor.Undo.FlushUndoRecordObjects();
if (root.GetComponentsInChildren<TownPlacedAsset>().Length != 0) throw new System.Exception("Erase left authored objects");
UnityEditor.Undo.PerformUndo();
var after = root.GetComponentsInChildren<TownPlacedAsset>();
if (after.Length != before || !after.Any(x => x.GetComponentInChildren<PickupStop>()?.stopId == identity))
    throw new System.Exception("Erase undo failed to restore identities");
UnityEngine.Physics.SyncTransforms();
string report = "PASS: property move keeps " + identity + ", native move undo/redo, eraser undo restores all " + erased + " objects and their household IDs.\nOriginal " + original + "; destination " + destination;
System.IO.File.WriteAllText("Logs/TownAuthoringQA/undo.txt", report);
return report;
