if (!SaveRoot.IsOverridden || !UnityEngine.Application.isPlaying)
    throw new System.InvalidOperationException("Isolated live test session required.");
if (DepotRowWorld.Transitioning || SceneController.IsTransitioning)
    throw new System.InvalidOperationException("Wait for the depot/scene transition to finish before probing road physics.");
var pc=PlayerController.Local;
var truck=pc != null ? pc.currentTruck : null;
if(truck==null||!truck.IsLocalDriver||!truck.IsOwner||!truck.IsLocalPlayerSeated||truck.InputOverride!=null)
    throw new System.InvalidOperationException("Seat the local owner through normal interaction first.");
var start=new UnityEngine.Pose(truck.transform.position,truck.transform.rotation);
var wheels=truck.GetComponentsInChildren<UnityEngine.WheelCollider>(true);
var results=new System.Collections.Generic.List<object>();
float command=0;
System.Func<UnityEngine.Vector2> input=()=>new UnityEngine.Vector2(0,command);
System.Collections.IEnumerator Probe(){
    truck.InputOverride=input;
    try{
        foreach(string id in new[]{"home","west-bridge"}){
            var road=CoastalCityLayout.Road(id);
            var pose=road.AtNormalized(id=="home"?.2f:.25f);
            var lane=pose.position+pose.rotation*UnityEngine.Vector3.right*2.5f;
            DepotRowWorld.TeleportTruck(truck,lane+UnityEngine.Vector3.up*.15f,pose.rotation);
            command=0;yield return new UnityEngine.WaitForSeconds(1.5f);
            int groundedStart=wheels.Count(w=>w.isGrounded), minGrounded=groundedStart;
            var from=truck.transform.position;
            command=1;
            float elapsed=0,maxTilt=0;
            while(elapsed<5f){
                minGrounded=UnityEngine.Mathf.Min(minGrounded,wheels.Count(w=>w.isGrounded));
                maxTilt=UnityEngine.Mathf.Max(maxTilt,UnityEngine.Vector3.Angle(truck.transform.up,UnityEngine.Vector3.up));
                elapsed+=UnityEngine.Time.deltaTime;yield return null;
            }
            command=0;
            results.Add(new{road=id,distance=UnityEngine.Vector3.Distance(from,truck.transform.position),groundedStart,minGrounded,maxTilt,
                start=from.ToString("F2"),end=truck.transform.position.ToString("F2"),speed=truck.Body.linearVelocity.magnitude});
        }
    }finally{
        command=0;
        if(truck!=null&&truck.InputOverride==input)truck.InputOverride=null;
        if(truck!=null&&truck.IsOwner&&truck.IsLocalDriver)DepotRowWorld.TeleportTruck(truck,start.position,start.rotation);
        System.IO.File.WriteAllText(System.IO.Path.Combine(SaveRoot.Path,"coastal-drive-smoke.json"),Newtonsoft.Json.JsonConvert.SerializeObject(results,Newtonsoft.Json.Formatting.Indented));
    }
}
pc.StartCoroutine(Probe());
return "Started real-wheel-collider smoke test on town road and river bridge; read coastal-drive-smoke.json in isolated root.";
