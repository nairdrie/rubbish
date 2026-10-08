// Read-only live-scene audit; does not save scenes, rebuild, enter Play or touch player saves.
var scene=TownPlacementTools.Scene();
if(!scene.isLoaded)throw new System.Exception("Open the town first.");
var root=scene.GetRootGameObjects().First(g=>g.name=="NeighborhoodGen");
object BoundsOf(UnityEngine.Transform source,UnityEngine.Transform frame)
{
    bool any=false;var b=new UnityEngine.Bounds();
    foreach(var r in source.GetComponentsInChildren<UnityEngine.Renderer>(true))
    {
        if(r.GetComponentInParent<CoastalYardBoundary>()!=null || r is UnityEngine.ParticleSystemRenderer || !r.enabled)continue;
        var lb=r.localBounds;
        for(int x=0;x<2;x++)for(int y=0;y<2;y++)for(int z=0;z<2;z++)
        {
            var p=frame.InverseTransformPoint(r.transform.TransformPoint(new UnityEngine.Vector3(x==0?lb.min.x:lb.max.x,y==0?lb.min.y:lb.max.y,z==0?lb.min.z:lb.max.z)));
            if(!any){b=new UnityEngine.Bounds(p,UnityEngine.Vector3.zero);any=true;}else b.Encapsulate(p);
        }
    }
    return new {min=new[]{b.min.x,b.min.y,b.min.z},max=new[]{b.max.x,b.max.y,b.max.z}};
}
var homes=TownAuthoringScope.Components<PickupStop>(root.transform).Where(s=>s.District.id==Districts.MildlyPleasant).OrderBy(s=>s.stopId).Select(s=>
{
    var a=s.GetComponentInParent<CityRoadAnchor>();var r=s.GetComponent<CoastalHouseRoute>();
    return new {id=s.stopId,road=a.roadId,u=a.u,lateral=a.lateral,yaw=a.yawOffset,position=new[]{a.transform.position.x,a.transform.position.z},model=r.buildingRoot.name,building=BoundsOf(r.buildingRoot,a.transform),lot=BoundsOf(a.transform,a.transform)};
}).ToArray();
var result=new {playing=UnityEditor.EditorApplication.isPlaying,dirty=scene.isDirty,homes,
    authored=TownPlacementTools.Root()?.childCount??0,
    anchors=root.GetComponentsInChildren<CityRoadAnchor>(true).Where(a=>a.GetComponentInChildren<PickupStop>(true)==null).Select(a=>new{id=a.stableId,road=a.roadId,u=a.u,lateral=a.lateral}).ToArray()};
System.IO.Directory.CreateDirectory("Logs/AcresLayoutQA");
System.IO.File.WriteAllText("Logs/AcresLayoutQA/after.json",Newtonsoft.Json.JsonConvert.SerializeObject(result,Newtonsoft.Json.Formatting.Indented));
return new {result.playing,result.dirty,count=homes.Length,result.authored};
