// Read-only candidate probe: no scene objects are created, changed or synchronized.
var scene = UnityEngine.SceneManagement.SceneManager.GetSceneByPath("Assets/Scenes/Neighborhood.unity");
var town = scene.GetRootGameObjects().First(g => g.name == "NeighborhoodGen").transform;
var roads = CoastalCityLayout.Roads;
var routes = town.GetComponentsInChildren<CoastalHouseRoute>(true);
var infill = town.GetComponentsInChildren<CoastalInfillSite>(true);
var results = new System.Collections.Generic.List<object>();
bool Clip(float start, float delta, float min, float max, ref float lo, ref float hi)
{
    if(UnityEngine.Mathf.Abs(delta)<.00001f)return start>=min&&start<=max;
    float a=(min-start)/delta,b=(max-start)/delta;
    lo=UnityEngine.Mathf.Max(lo,UnityEngine.Mathf.Min(a,b));hi=UnityEngine.Mathf.Min(hi,UnityEngine.Mathf.Max(a,b));return lo<=hi;
}
foreach(var id in new[]{"carpool","juicebox","meadow-loop","hill-loop","monocle","bluff-loop","bluff-high","offshore"})
{
    var road = CoastalCityLayout.Road(id); var choices=new System.Collections.Generic.List<object>();
    foreach(int side in new[]{-1,1}) foreach(float width in new[]{36f,28f})
    for(int step=2;step<=18;step++)
    {
        float u=step*.05f,depth=width,setback=6;
        var pose=CityRoadAnchor.Resolve(road,u,side*(road.halfWidth+3+setback),0,side*90);
        var inverse=UnityEngine.Quaternion.Inverse(pose.rotation);
        bool Segment(UnityEngine.Vector3 a,UnityEngine.Vector3 b,float margin)
        {
            a=inverse*(a-pose.position);b=inverse*(b-pose.position);float lo=0,hi=1;
            return Clip(a.x,b.x-a.x,-width*.5f-margin,width*.5f+margin,ref lo,ref hi)&&Clip(a.z,b.z-a.z,-margin,depth+margin,ref lo,ref hi);
        }
        bool clear=true;
        foreach(var r in roads)for(int i=1;i<r.points.Length&&clear;i++)if(Segment(r.points[i-1],r.points[i],r.halfWidth+3.1f))clear=false;
        foreach(var route in routes)
        {
            var stop=route.GetComponent<PickupStop>();if(stop!=null&&Segment(stop.transform.position,stop.transform.position,3))clear=false;
            var previous=route.door;
            foreach(var bend in route.bends??System.Array.Empty<UnityEngine.Vector3>()){if(Segment(previous,bend,1.4f))clear=false;previous=bend;}
            if(Segment(previous,route.pathEnd,1.4f)||route.hasDrive&&Segment(route.ramp,route.top,2.4f))clear=false;
        }
        foreach(var other in infill)
        {
            var corners=other.Corners();for(int i=0;i<4;i++)if(Segment(corners[i],corners[(i+1)%4],.3f))clear=false;
            if(other.IntersectsSegment(pose.position,pose.position,0))clear=false;
        }
        if(!clear)continue;
        var center=pose.position+pose.rotation*new UnityEngine.Vector3(0,15.4f,depth*.5f);
        var hits=UnityEngine.Physics.OverlapBox(center,new UnityEngine.Vector3(width*.5f,15,depth*.5f),pose.rotation,~0,UnityEngine.QueryTriggerInteraction.Ignore);
        foreach(var hit in hits)
        {
            if(!hit.transform.IsChildOf(town)||hit.attachedRigidbody!=null||hit.bounds.max.y<=.3f)continue;
            // Full-build scenery is generated after reserved infill; loose background trees are not blockers.
            var nature=town.Find("Nature");if(nature!=null&&hit.transform.IsChildOf(nature))continue;
            clear=false;break;
        }
        if(clear)choices.Add(new{roadId=id,u,side,width,depth,setback,world=new[]{pose.position.x,pose.position.z}});
    }
    results.Add(new{road=id,candidates=choices});
}
return results;
