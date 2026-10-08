// Inspect the actual saved/generated road meshes in an isolated render scene, not a diagram.
if (UnityEditor.EditorApplication.isPlaying) throw new System.Exception("Exit Play mode first.");
const string phase = "after";
var town = TownPlacementTools.Scene();
if (!town.isLoaded) throw new System.Exception("Open town first.");
var root = town.GetRootGameObjects().First(g => g.name == "NeighborhoodGen").transform;
var preview = new UnityEditor.PreviewRenderUtility();
var shots = new[] {
    new { name="t-junction", p=new UnityEngine.Vector3(123.5f,0,58.5f) },
    new { name="crossroads", p=new UnityEngine.Vector3(201.5f,0,165.75f) },
    new { name="corner", p=new UnityEngine.Vector3(201.5f,0,260f) },
    new { name="bridge", p=new UnityEngine.Vector3(123.5f,0,260f) },
    new { name="oblique", p=new UnityEngine.Vector3(367.25f,0,364f) }
};
System.IO.Directory.CreateDirectory("Logs/RoadJunctionQA");
try
{
    foreach(var filter in root.Find("CoastalRoads").GetComponentsInChildren<UnityEngine.MeshFilter>(true))
    {
        var renderer=filter.GetComponent<UnityEngine.MeshRenderer>(); if(!renderer || !renderer.enabled) continue;
        if(!shots.Any(s=>UnityEngine.Vector3.Distance(renderer.bounds.ClosestPoint(s.p),s.p)<48)) continue;
        var go=new UnityEngine.GameObject(filter.name); go.hideFlags=UnityEngine.HideFlags.HideAndDontSave;
        go.transform.SetPositionAndRotation(filter.transform.position,filter.transform.rotation); go.transform.localScale=filter.transform.lossyScale;
        go.AddComponent<UnityEngine.MeshFilter>().sharedMesh=filter.sharedMesh;
        go.AddComponent<UnityEngine.MeshRenderer>().sharedMaterials=renderer.sharedMaterials;
        preview.AddSingleGO(go);
    }
    preview.camera.orthographic=true; preview.camera.orthographicSize=28; preview.camera.nearClipPlane=.1f; preview.camera.farClipPlane=200;
    preview.camera.backgroundColor=new UnityEngine.Color(.24f,.36f,.19f); preview.camera.clearFlags=UnityEngine.CameraClearFlags.SolidColor;
    preview.lights[0].intensity=1.5f; preview.lights[0].transform.rotation=UnityEngine.Quaternion.Euler(50,-30,0);
    preview.lights[1].intensity=.5f; preview.ambientColor=new UnityEngine.Color(.5f,.5f,.5f);
    foreach(var shot in shots)
    {
        preview.camera.transform.SetPositionAndRotation(shot.p+UnityEngine.Vector3.up*80,UnityEngine.Quaternion.Euler(90,0,0));
        preview.BeginStaticPreview(new UnityEngine.Rect(0,0,800,800));preview.Render(true);
        var image=preview.EndStaticPreview();
        try {System.IO.File.WriteAllBytes("Logs/RoadJunctionQA/"+phase+"-"+shot.name+".png",image.EncodeToPNG());}
        finally {UnityEngine.Object.DestroyImmediate(image);}
    }
}
finally {preview.Cleanup();}
return "Rendered "+shots.Length+" "+phase+" junction views";
