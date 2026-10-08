// Read-only edit-mode city review. Produces files under Logs, never touches a player save.
if (UnityEditor.EditorApplication.isPlaying) throw new System.InvalidOperationException("Exit play mode first.");
UnityEditor.EditorApplication.delayCall += () => {
    var opened = new System.Collections.Generic.List<UnityEngine.SceneManagement.Scene>();
    var temporary = new System.Collections.Generic.List<UnityEngine.GameObject>();
    var before = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
    try {
        foreach (var path in new[]{"Assets/Scenes/Neighborhood.unity", "Assets/Scenes/DepotRow.unity"}) {
            var scene = UnityEngine.SceneManagement.SceneManager.GetSceneByPath(path);
            if (!scene.IsValid() || !scene.isLoaded) { scene = UnityEditor.SceneManagement.EditorSceneManager.OpenScene(path, UnityEditor.SceneManagement.OpenSceneMode.Additive); opened.Add(scene); }
        }
        var lightGo = new UnityEngine.GameObject("Temporary city review sun"); temporary.Add(lightGo);
        var light = lightGo.AddComponent<UnityEngine.Light>(); light.type = UnityEngine.LightType.Directional;
        light.intensity = 1.5f; light.shadows = UnityEngine.LightShadows.Soft; lightGo.transform.rotation = UnityEngine.Quaternion.Euler(48,-35,0);
        var shots = new[] {
            new {name="city-overview", eye=new UnityEngine.Vector3(530,1180,380), look=new UnityEngine.Vector3(530,0,570), ortho=650f},
            new {name="depot-and-acres", eye=new UnityEngine.Vector3(270,155,-60), look=new UnityEngine.Vector3(240,0,135), ortho=0f},
            new {name="river-and-commons", eye=new UnityEngine.Vector3(290,190,290), look=new UnityEngine.Vector3(320,0,530), ortho=0f},
            new {name="skyline", eye=new UnityEngine.Vector3(895,165,365), look=new UnityEngine.Vector3(800,20,540), ortho=0f},
            new {name="rich-neighborhoods", eye=new UnityEngine.Vector3(600,410,570), look=new UnityEngine.Vector3(595,0,855), ortho=0f}
        };
        System.IO.Directory.CreateDirectory("Logs/CoastalCityQA");
        foreach(var shot in shots) {
            var go = new UnityEngine.GameObject("Temporary city review camera"); temporary.Add(go);
            var cam = go.AddComponent<UnityEngine.Camera>(); cam.enabled=false; cam.useOcclusionCulling=false;
            cam.clearFlags=UnityEngine.CameraClearFlags.SolidColor;cam.backgroundColor=new UnityEngine.Color(.56f,.73f,.83f);
            cam.nearClipPlane=.3f;cam.farClipPlane=3000;cam.fieldOfView=65;cam.aspect=1.5f;
            cam.orthographic=shot.ortho>0;cam.orthographicSize=shot.ortho;
            cam.transform.SetPositionAndRotation(shot.eye,UnityEngine.Quaternion.LookRotation(shot.look-shot.eye));
            var target=new UnityEngine.RenderTexture(1500,1000,24);var previous=UnityEngine.RenderTexture.active;
            UnityEngine.Texture2D pixels=null;
            try {
                target.Create();cam.targetTexture=target;
                UnityEngine.Rendering.RenderPipeline.SubmitRenderRequest(cam,new UnityEngine.Rendering.RenderPipeline.StandardRequest{destination=target});
                UnityEngine.RenderTexture.active=target;pixels=new UnityEngine.Texture2D(1500,1000,UnityEngine.TextureFormat.RGB24,false);
                pixels.ReadPixels(new UnityEngine.Rect(0,0,1500,1000),0,0);pixels.Apply();
                System.IO.File.WriteAllBytes("Logs/CoastalCityQA/"+shot.name+".png",pixels.EncodeToPNG());
            } finally {cam.targetTexture=null;UnityEngine.RenderTexture.active=previous;target.Release();UnityEngine.Object.DestroyImmediate(target);if(pixels)UnityEngine.Object.DestroyImmediate(pixels);}
        }
        System.IO.File.WriteAllText("Logs/CoastalCityQA/photos-result.txt","Completed five city views.");
    } catch(System.Exception ex) {System.IO.File.WriteAllText("Logs/CoastalCityQA/photos-result.txt",ex.ToString());UnityEngine.Debug.LogException(ex);}
    finally {
        foreach(var go in temporary) if(go)UnityEngine.Object.DestroyImmediate(go);
        if(before.IsValid()&&before.isLoaded)UnityEngine.SceneManagement.SceneManager.SetActiveScene(before);
        foreach(var scene in opened)if(scene.IsValid()&&scene.isLoaded)UnityEditor.SceneManagement.EditorSceneManager.CloseScene(scene,true);
    }
};
return "City views scheduled; read Logs/CoastalCityQA/photos-result.txt";
