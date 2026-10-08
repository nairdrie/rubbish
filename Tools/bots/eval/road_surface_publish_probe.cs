if (UnityEditor.EditorApplication.isPlaying || TownPlacementTools.Scene().isDirty)
    throw new System.Exception("Start with a saved town in Edit Mode; this publishes only derived road repairs.");
var town=TownPlacementTools.Scene();
var root=town.GetRootGameObjects().First(g=>g.name=="NeighborhoodGen");
string[] Homes() => TownAuthoringScope.Components<PickupStop>(root.transform).Select(s=>s.stopId+"|"+s.districtId+"|"+s.transform.position.ToString("R")).OrderBy(s=>s).ToArray();
string[] Lamps() => root.transform.Find("CoastalRoads").GetComponentsInChildren<Knockable>(true).Select(k=>k.id+"|"+k.transform.position.ToString("R")).OrderBy(s=>s).ToArray();
var homes=Homes();var lamps=Lamps();
string blueprint=System.IO.File.ReadAllText(CoastalCityLayout.BlueprintPath);
var report=TownGameplayAuthoring.Publish();
if(!homes.SequenceEqual(Homes()))throw new System.Exception("Household IDs/districts/poses changed unexpectedly.");
if(!lamps.SequenceEqual(Lamps()))throw new System.Exception("Unchanged road lamp IDs/poses changed unexpectedly.");
if(blueprint!=System.IO.File.ReadAllText(CoastalCityLayout.BlueprintPath))throw new System.Exception("The street blueprint changed unexpectedly.");
var errors=CoastalCityValidation.ValidateGeneratedRoads(root.transform);
if(errors.Count>0)throw new System.Exception(string.Join("\n",errors));
var fingerprint=CityGeneratedRoad.SurfaceFingerprint(CoastalCityLayout.Roads);
if(root.GetComponentsInChildren<CityGeneratedRoad>(true).Any(g=>g.surfaceFingerprint!=fingerprint))throw new System.Exception("A road still has stale junction surfaces.");
report+="\nPreserved "+homes.Length+" household identities/districts/poses, "+lamps.Length+" generated lamp identities/poses, and the exact street blueprint. All road surfaces current.";
System.IO.Directory.CreateDirectory("Logs/RoadJunctionQA");System.IO.File.WriteAllText("Logs/RoadJunctionQA/publish.txt",report);
return report;
