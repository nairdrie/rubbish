// Actual authored mesh/material review in an isolated preview scene; no gameplay/save changes.
const string phase = "after";
var scene=TownPlacementTools.Scene();
var preview=new UnityEditor.PreviewRenderUtility();
System.IO.Directory.CreateDirectory("Logs/AcresLayoutQA");
try {
    var region=new UnityEngine.Bounds(new UnityEngine.Vector3(267,20,154),new UnityEngine.Vector3(380,100,310));
    var scenes=new[]{scene,UnityEngine.SceneManagement.SceneManager.GetSceneByPath("Assets/Scenes/DepotRow.unity")};
    int count=0;
    foreach(var s in scenes)if(s.isLoaded)foreach(var root in s.GetRootGameObjects())
    foreach(var f in root.GetComponentsInChildren<UnityEngine.MeshFilter>(true)) {
        var r=f.GetComponent<UnityEngine.MeshRenderer>();
        if(!r||!r.enabled||!r.gameObject.activeInHierarchy||!region.Intersects(r.bounds)||!f.sharedMesh)continue;
        var go=new UnityEngine.GameObject(f.name);go.hideFlags=UnityEngine.HideFlags.HideAndDontSave;
        go.transform.SetPositionAndRotation(f.transform.position,f.transform.rotation);go.transform.localScale=f.transform.lossyScale;
        go.AddComponent<UnityEngine.MeshFilter>().sharedMesh=f.sharedMesh;
        go.AddComponent<UnityEngine.MeshRenderer>().sharedMaterials=r.sharedMaterials;preview.AddSingleGO(go);count++;
    }
    preview.camera.nearClipPlane=.1f;preview.camera.farClipPlane=1100;preview.camera.fieldOfView=65;
    preview.camera.backgroundColor=new UnityEngine.Color(.57f,.74f,.84f);preview.camera.clearFlags=UnityEngine.CameraClearFlags.SolidColor;
    preview.lights[0].intensity=1.25f;preview.lights[0].transform.rotation=UnityEngine.Quaternion.Euler(55,-30,0);
    preview.lights[1].intensity=.4f;preview.ambientColor=new UnityEngine.Color(.48f,.48f,.48f);
    var shots=new[]{
        new{name="overview",eye=new UnityEngine.Vector3(265,450,155),look=new UnityEngine.Vector3(265,0,155),ortho=140f},
        new{name="depot-backyards",eye=new UnityEngine.Vector3(100,80,10),look=new UnityEngine.Vector3(180,0,160),ortho=0f},
        new{name="elm-street",eye=new UnityEngine.Vector3(184,4,64),look=new UnityEngine.Vector3(184,3,155),ortho=0f}
    };
    foreach(var shot in shots){
        preview.camera.orthographic=shot.ortho>0;preview.camera.orthographicSize=shot.ortho;
        preview.camera.transform.SetPositionAndRotation(shot.eye,UnityEngine.Quaternion.LookRotation(shot.look-shot.eye));
        preview.BeginStaticPreview(new UnityEngine.Rect(0,0,1600,1000));preview.Render(true);var image=preview.EndStaticPreview();
        try{System.IO.File.WriteAllBytes("Logs/AcresLayoutQA/"+phase+"-"+shot.name+".png",image.EncodeToPNG());}
        finally{UnityEngine.Object.DestroyImmediate(image);}
    }
    return "Rendered "+phase+" from "+count+" actual mesh objects.";
}finally{preview.Cleanup();}
