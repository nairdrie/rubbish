// Read-only diagnostic: no transform/scene changes or physics sync.
var town = UnityEngine.SceneManagement.SceneManager.GetSceneByPath("Assets/Scenes/Neighborhood.unity").GetRootGameObjects().FirstOrDefault(g => g.name == "NeighborhoodGen")?.transform;
if (town == null) throw new System.InvalidOperationException("Loaded Neighborhood required.");
var target = town.GetComponentsInChildren<CityRoadAnchor>(true).Single(a => a.stableId == "sh-oldmoney-328");
var own = new System.Collections.Generic.HashSet<UnityEngine.Collider>(target.GetComponentsInChildren<UnityEngine.Collider>(true).Where(c => c.enabled && !c.isTrigger && c.gameObject.activeInHierarchy));
var current = new System.Collections.Generic.List<object>();
var repair = new System.Collections.Generic.List<object>();
float[] V(UnityEngine.Vector3 p) => new[] { p.x, p.y, p.z };
foreach (var road in CoastalCityLayout.Roads)
{
    int n = UnityEngine.Mathf.Max(1,UnityEngine.Mathf.CeilToInt(road.Length/2.5f));
    float lane = UnityEngine.Mathf.Min(StreetGraph.LaneOffset,road.halfWidth*.5f);
    for(int i=0;i<=n;i++)
    {
        var p=road.At(road.Length*i/n);
        for(int channel=-1;channel<=1;channel++)
        {
            var at=p.position+p.rotation*UnityEngine.Vector3.right*(channel*lane)+UnityEngine.Vector3.up*1.8f;
            var hits=UnityEngine.Physics.OverlapBox(at,new UnityEngine.Vector3(.85f,1.5f,1.2f),p.rotation,~0,UnityEngine.QueryTriggerInteraction.Ignore).Where(c=>own.Contains(c)).ToArray();
            if(hits.Length>0)current.Add(new {road=road.id,name=road.name,i,n,channel,at=V(at),angle=p.rotation.eulerAngles.y,hits=hits.Select(c=>new {c.name,boundsMin=V(c.bounds.min),boundsMax=V(c.bounds.max),rigid=c.attachedRigidbody!=null}).ToArray()});
        }
    }
    n=UnityEngine.Mathf.Max(1,UnityEngine.Mathf.CeilToInt(road.Length/2f));
    for(int i=0;i<=n;i++)
    {
        var p=road.At(road.Length*i/n);
        var at=p.position+UnityEngine.Vector3.up*1.8f;
        var hits=UnityEngine.Physics.OverlapBox(at,new UnityEngine.Vector3(UnityEngine.Mathf.Max(.5f,road.halfWidth-.12f),1.55f,1.05f),p.rotation,~0,UnityEngine.QueryTriggerInteraction.Ignore).Where(c=>own.Contains(c)).ToArray();
        if(hits.Length>0)repair.Add(new {road=road.id,i,n,at=V(at),hits=hits.Select(c=>c.name).ToArray()});
    }
}
return Newtonsoft.Json.JsonConvert.SerializeObject(new {target.stableId,target.roadId,target.u,position=V(target.transform.position),ownColliders=own.Count,validatorHits=current,repairHits=repair},Newtonsoft.Json.Formatting.Indented);
