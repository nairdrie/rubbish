// Final static checks. Every property is fixed: the parcel checker may only validate, never search/move one.
if(UnityEditor.EditorApplication.isPlaying)throw new System.Exception("Edit Mode only.");
var scene=TownPlacementTools.Scene();var root=scene.GetRootGameObjects().First(g=>g.name=="NeighborhoodGen").transform;
if(scene.isDirty)throw new System.Exception("Expected the published clean scene.");
var anchors=root.GetComponentsInChildren<CityRoadAnchor>(true);
var locked=new System.Collections.Generic.HashSet<string>(anchors.Select(a=>a.stableId));
var parcelReport=CoastalParcelPlacement.RepairWithFixedLots(root,locked);
var rearHomes=new[]{"birch-18","birch-19","birch-27","birch-28","birch-36","birch-39"};
var yards=root.GetComponentsInChildren<CoastalYardBoundary>(true);
foreach(var id in rearHomes){
    var a=anchors.Single(a=>a.stableId==id);
    if(a.roadId!="elm" || UnityEngine.Vector3.Dot(a.transform.forward,UnityEngine.Vector3.right)<.999f)
        throw new System.Exception(id+" does not face east into Acres.");
    if(!yards.Any(y=>y.stopIds.Contains(id)))throw new System.Exception(id+" lacks a complete backyard boundary.");
}
var graph=root.GetComponentInChildren<WorldLifeAnchor>(true).graph;
var errors=CoastalCityValidation.Validate(root,graph);
if(errors.Count>0)throw new System.Exception(string.Join("\n",errors));
if(!TownWorldSignatureBuilder.IsCurrent(out var reason))throw new System.Exception(reason);
if(scene.isDirty || SaveRoot.IsOverridden)throw new System.Exception("Unexpected scene/save test state.");
var report=parcelReport+"\nAll six depot-edge homes face east and have complete yard boundaries. Navigation, road envelopes, map signature and city validation passed. Edit Mode; clean scene; no save override.";
System.IO.File.WriteAllText("Logs/AcresLayoutQA/final-checks.txt",report);return report;
