if(UnityEditor.EditorApplication.isPlaying)throw new System.Exception("Edit Mode only.");
var scene=TownPlacementTools.Scene();var root=scene.GetRootGameObjects().First(g=>g.name=="NeighborhoodGen").transform;
if(scene.isDirty)throw new System.Exception("Expected a published clean scene.");
var anchors=root.GetComponentsInChildren<CityRoadAnchor>(true);
var parcelReport=CoastalParcelPlacement.RepairWithFixedLots(root,new System.Collections.Generic.HashSet<string>(anchors.Select(a=>a.stableId)));
int groundChecks=0,truckChecks=0;
foreach(var id in new[]{"carpool-court","juicebox-court"}) {
    var road=CoastalCityLayout.Road(id);
    for(int i=0;i<64;i++) {
        var p=road.AtNormalized(i/64f);
        foreach(float side in new[]{-2.5f,0,2.5f}) {
            var point=p.position+p.rotation*UnityEngine.Vector3.right*side;
            var hits=UnityEngine.Physics.RaycastAll(point+UnityEngine.Vector3.up*5,UnityEngine.Vector3.down,7,~0,UnityEngine.QueryTriggerInteraction.Ignore);
            var ground=hits.Where(h=>h.collider.GetComponentInParent<CityGeneratedRoad>()!=null).OrderBy(h=>h.distance).FirstOrDefault();
            if(!ground.collider||!ground.collider.name.StartsWith("Road_"))throw new System.Exception(id+": pavement across lane or missing road at "+point);
            groundChecks++;
            if(side==0)continue;
            var blockers=UnityEngine.Physics.OverlapBox(point+UnityEngine.Vector3.up*1.8f,new UnityEngine.Vector3(1.45f,1.45f,4.5f),p.rotation,~0,UnityEngine.QueryTriggerInteraction.Ignore)
                .Where(c=>c.transform.IsChildOf(root)&&c.bounds.max.y>.3f&&c.GetComponentInParent<DistrictGate>()==null&&c.GetComponentInParent<Knockable>()==null).ToArray();
            if(blockers.Length>0)throw new System.Exception(id+": 9m truck footprint blocked by "+string.Join(",",blockers.Select(c=>c.name)));
            truckChecks++;
        }
    }
}
var errors=CoastalCityValidation.Validate(root,root.GetComponentInChildren<WorldLifeAnchor>(true).graph);
if(errors.Count>0)throw new System.Exception(string.Join("\n",errors));
if(!TownWorldSignatureBuilder.IsCurrent(out var reason))throw new System.Exception(reason);
if(scene.isDirty||SaveRoot.IsOverridden)throw new System.Exception("Unexpected scene/save test state.");
var report=parcelReport+"\n"+groundChecks+" road-surface rays and "+truckChecks+" static 9m-truck footprint checks passed around both courts. City/nav/signature validation passed; clean Edit Mode scene; no save override.";
System.IO.File.WriteAllText("Logs/MeadowsLayoutQA/final-checks.txt",report);return report;
