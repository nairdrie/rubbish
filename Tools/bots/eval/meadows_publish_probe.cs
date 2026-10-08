try {
if(UnityEditor.EditorApplication.isPlaying || TownPlacementTools.Scene().isDirty)
    throw new System.Exception("Start from the saved town in Edit Mode.");
UnityEditor.AssetDatabase.ImportAsset(CoastalCityLayout.BlueprintPath,UnityEditor.ImportAssetOptions.ForceUpdate);
UnityEditor.AssetDatabase.ImportAsset(CoastalResidentialLayout.RecipePath,UnityEditor.ImportAssetOptions.ForceUpdate);
CoastalCityLayout.Reload();
var previous=Newtonsoft.Json.JsonConvert.DeserializeObject<CoastalCityLayout.Blueprint>(System.IO.File.ReadAllText("Logs/MeadowsLayoutQA/before-blueprint.json"));
var operations=typeof(TownRoadDraftOperations);
operations.GetMethod("RejectGateReachabilityExpansion",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static).Invoke(null,new object[]{previous,CoastalCityLayout.Data});
var scene=TownPlacementTools.Scene();var root=scene.GetRootGameObjects().First(g=>g.name=="NeighborhoodGen").transform;
var stops=TownAuthoringScope.Components<PickupStop>(root);
string[] Ids()=>TownAuthoringScope.Components<PickupStop>(root).Select(s=>s.stopId+"|"+s.districtId+"|"+s.bagCount+"|"+s.wealth).OrderBy(s=>s).ToArray();
var ids=Ids();
string[] OtherInfill()=>root.GetComponentsInChildren<CoastalInfillSite>(true).Where(s=>CoastalCityLayout.Road(s.GetComponent<CityRoadAnchor>().roadId).districtId!=Districts.MinivanMeadows).Select(s=>s.siteId+"|"+s.transform.position.ToString("R")+"|"+s.transform.rotation.ToString("R")).OrderBy(s=>s).ToArray();
var infill=OtherInfill();
var others=stops.Where(s=>s.District.id!=Districts.MinivanMeadows).Select(s=>new{id=s.stopId,p=s.transform.position,q=s.transform.rotation}).ToArray();
var gates=root.GetComponentsInChildren<DistrictGate>(true).Select(g=>g.transform.position).ToArray();
var report=NeighborhoodBuilder.ApplyResidentialBlocks();
if(!ids.SequenceEqual(Ids()))throw new System.Exception("Household identities, district or trash rules changed.");
if(!infill.SequenceEqual(OtherInfill()))throw new System.Exception("Another district's decorative parcel changed.");
var now=TownAuthoringScope.Components<PickupStop>(root).ToDictionary(s=>s.stopId);
foreach(var old in others)if(UnityEngine.Vector3.Distance(old.p,now[old.id].transform.position)>.001f||UnityEngine.Quaternion.Angle(old.q,now[old.id].transform.rotation)>.01f)
    throw new System.Exception("Another district's home moved: "+old.id);
var afterGates=root.GetComponentsInChildren<DistrictGate>(true).Select(g=>g.transform.position).ToArray();
if(gates.Any(g=>!afterGates.Any(p=>UnityEngine.Vector3.Distance(g,p)<.01f)))throw new System.Exception("A progression gate moved.");
var errors=CoastalCityValidation.Validate(root,root.GetComponentInChildren<WorldLifeAnchor>(true).graph);
if(errors.Count>0)throw new System.Exception(string.Join("\n",errors));
report+="\nPreserved "+ids.Length+" household identities/trash rules; all "+others.Length+" homes outside Meadows and all gate positions. All 31 gate-lock combinations preserve permit reachability. City validation passed.";
System.IO.File.WriteAllText("Logs/MeadowsLayoutQA/publish.txt",report);return report;
}catch(System.Exception error){System.IO.File.WriteAllText("Logs/MeadowsLayoutQA/publish-error.txt",error.ToString());throw;}
