// Edit-mode visual QA. Does not write scenes, gameplay settings or player saves.
if (UnityEditor.EditorApplication.isPlaying) throw new System.InvalidOperationException("Exit Play mode first.");
    var opened = new System.Collections.Generic.List<UnityEngine.SceneManagement.Scene>();
    var temporary = new System.Collections.Generic.List<UnityEngine.GameObject>();
    var before = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
    try {
        foreach (var path in new[]{"Assets/Scenes/Neighborhood.unity", "Assets/Scenes/DepotRow.unity"}) {
            var scene = UnityEngine.SceneManagement.SceneManager.GetSceneByPath(path);
            if (!scene.IsValid() || !scene.isLoaded) { scene = UnityEditor.SceneManagement.EditorSceneManager.OpenScene(path, UnityEditor.SceneManagement.OpenSceneMode.Additive); opened.Add(scene); }
        }
        var sun = new UnityEngine.GameObject("Temporary density review sun"); temporary.Add(sun);
        var light = sun.AddComponent<UnityEngine.Light>(); light.type=UnityEngine.LightType.Directional;
        light.intensity=1.5f; light.shadows=UnityEngine.LightShadows.Soft; sun.transform.rotation=UnityEngine.Quaternion.Euler(48,-35,0);
        var depot = CoastalCityLayout.DepotDoor(0);
        var shots = new[] {
            new {name="overview", eye=new UnityEngine.Vector3(405,1100,460), look=new UnityEngine.Vector3(405,0,460), ortho=530f},
            new {name="depot-exit", eye=depot+new UnityEngine.Vector3(4,3.4f,0), look=depot+new UnityEngine.Vector3(65,2,0), ortho=0f},
            new {name="depot-street", eye=new UnityEngine.Vector3(124,5,60), look=new UnityEngine.Vector3(134,3,190), ortho=0f},
            new {name="acres-yards", eye=new UnityEngine.Vector3(210,125,40), look=new UnityEngine.Vector3(195,0,150), ortho=0f},
            new {name="acres-overview", eye=new UnityEngine.Vector3(267,500,160), look=new UnityEngine.Vector3(267,0,160), ortho=190f},
            new {name="commons-street", eye=new UnityEngine.Vector3(96,4,469), look=new UnityEngine.Vector3(265,3,469), ortho=0f},
            new {name="commons-aerial", eye=new UnityEngine.Vector3(270,175,300), look=new UnityEngine.Vector3(240,0,470), ortho=0f},
            new {name="meadows-aerial", eye=new UnityEngine.Vector3(630,170,-5), look=new UnityEngine.Vector3(630,0,170), ortho=0f},
            new {name="condos-aerial", eye=new UnityEngine.Vector3(585,180,310), look=new UnityEngine.Vector3(565,20,465), ortho=0f},
            new {name="rich-neighborhoods", eye=new UnityEngine.Vector3(420,380,485), look=new UnityEngine.Vector3(440,0,720), ortho=0f}
        };
        System.IO.Directory.CreateDirectory("Logs/CityDensityQA");
        foreach(var shot in shots) {
            var go=new UnityEngine.GameObject("Temporary density review camera");temporary.Add(go);
            var cam=go.AddComponent<UnityEngine.Camera>();cam.enabled=false;cam.useOcclusionCulling=false;
            cam.clearFlags=UnityEngine.CameraClearFlags.SolidColor;cam.backgroundColor=new UnityEngine.Color(.56f,.73f,.83f);
            cam.nearClipPlane=.3f;cam.farClipPlane=2200;cam.fieldOfView=65;cam.aspect=1.5f;
            cam.orthographic=shot.ortho>0;cam.orthographicSize=shot.ortho;
            cam.transform.SetPositionAndRotation(shot.eye,UnityEngine.Quaternion.LookRotation(shot.look-shot.eye));
            var target=new UnityEngine.RenderTexture(1500,1000,24);var previous=UnityEngine.RenderTexture.active;
            UnityEngine.Texture2D pixels=null;
            try {
                target.Create();cam.targetTexture=target;
                UnityEngine.Rendering.RenderPipeline.SubmitRenderRequest(cam,new UnityEngine.Rendering.RenderPipeline.StandardRequest{destination=target});
                UnityEngine.RenderTexture.active=target;pixels=new UnityEngine.Texture2D(1500,1000,UnityEngine.TextureFormat.RGB24,false);
                pixels.ReadPixels(new UnityEngine.Rect(0,0,1500,1000),0,0);pixels.Apply();
                System.IO.File.WriteAllBytes("Logs/CityDensityQA/"+shot.name+".png",pixels.EncodeToPNG());
            } finally {cam.targetTexture=null;UnityEngine.RenderTexture.active=previous;target.Release();UnityEngine.Object.DestroyImmediate(target);if(pixels)UnityEngine.Object.DestroyImmediate(pixels);}
        }
        System.IO.File.WriteAllText("Logs/CityDensityQA/photos-result.txt","Completed ten density views.");
    } catch(System.Exception ex) {System.IO.File.WriteAllText("Logs/CityDensityQA/photos-result.txt",ex.ToString());UnityEngine.Debug.LogException(ex);}
    finally {
        foreach(var go in temporary)if(go)UnityEngine.Object.DestroyImmediate(go);
        if(before.IsValid()&&before.isLoaded)UnityEngine.SceneManagement.SceneManager.SetActiveScene(before);
        foreach(var scene in opened)if(scene.IsValid()&&scene.isLoaded)UnityEditor.SceneManagement.EditorSceneManager.CloseScene(scene,true);
    }
return "Density review completed; see Logs/CityDensityQA/photos-result.txt";
