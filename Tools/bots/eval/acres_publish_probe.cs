if(UnityEditor.EditorApplication.isPlaying || TownPlacementTools.Scene().isDirty)
    throw new System.Exception("Start from the saved town in Edit Mode.");
var scene=TownPlacementTools.Scene();
var root=scene.GetRootGameObjects().First(g=>g.name=="NeighborhoodGen").transform;
string[] Ids()=>TownAuthoringScope.Components<PickupStop>(root).Select(s=>s.stopId+"|"+s.districtId).OrderBy(s=>s).ToArray();
string[] OtherHomes()=>TownAuthoringScope.Components<PickupStop>(root).Where(s=>s.District.id!=Districts.MildlyPleasant).Select(s=>s.stopId+"|"+s.transform.position.ToString("R")+"|"+s.transform.rotation.ToString("R")).OrderBy(s=>s).ToArray();
var ids=Ids();var others=OtherHomes();
var gates=root.GetComponentsInChildren<DistrictGate>(true).Select(g=>g.transform.position).ToArray();
var park=root.Find("Park").position;
var report=NeighborhoodBuilder.ApplyResidentialBlocks();
if(!ids.SequenceEqual(Ids()))throw new System.Exception("Household identities changed.");
if(!others.SequenceEqual(OtherHomes()))throw new System.Exception("Another district's home moved unexpectedly.");
if(root.Find("Park").position!=park)throw new System.Exception("Park moved unexpectedly.");
var afterGates=root.GetComponentsInChildren<DistrictGate>(true).Select(g=>g.transform.position).ToArray();
if(gates.Any(g=>!afterGates.Any(p=>UnityEngine.Vector3.Distance(g,p)<.01f)))throw new System.Exception("A progression gate moved unexpectedly.");
var recipe=CoastalResidentialLayout.ResolveLive();
foreach(var pair in recipe){
    var a=root.GetComponentsInChildren<CityRoadAnchor>(true).Single(x=>x.stableId==pair.Key);
    if(a.roadId!=pair.Value.roadId||UnityEngine.Mathf.Abs(a.u-pair.Value.u)>.00001f)throw new System.Exception("Recipe not applied: "+pair.Key);
}
var errors=CoastalCityValidation.Validate(root,root.GetComponentInChildren<WorldLifeAnchor>(true).graph);
if(errors.Count>0)throw new System.Exception(string.Join("\n",errors));
report+="\nPreserved "+ids.Length+" household IDs/districts, all "+others.Length+" homes outside Acres, park and gate positions. Applied "+recipe.Count+" residential placements. City validation passed.";
System.IO.Directory.CreateDirectory("Logs/AcresLayoutQA");System.IO.File.WriteAllText("Logs/AcresLayoutQA/publish.txt",report);
return report;
