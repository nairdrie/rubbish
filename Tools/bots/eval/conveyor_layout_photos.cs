if (!SaveRoot.IsOverridden || !UnityEngine.Application.isPlaying)
    throw new System.InvalidOperationException("Isolated play-test save required.");
var rig = DepotRowScene.Lot(0).interior.GetComponentInChildren<GarageRig>(true);
float floor = rig.transform.InverseTransformPoint(rig.table.transform.position).y;
UnityEngine.Vector3 World(UnityEngine.Vector3 p) => rig.transform.TransformPoint(p + UnityEngine.Vector3.up * floor);
var shots = new[] {
    new {name="wall-conveyor-route", eye=new UnityEngine.Vector3(-4.5f,2.3f,-.2f), look=new UnityEngine.Vector3(4f,2.7f,.3f), fov=72f},
    new {name="kevin-wall-chute", eye=new UnityEngine.Vector3(1f,2f,-1.8f), look=new UnityEngine.Vector3(5.55f,2.2f,-4.35f), fov=58f},
};
var files = new System.Collections.Generic.List<string>();
foreach (var shot in shots)
{
    var go = new UnityEngine.GameObject("Conveyor QA camera");
    var cam = go.AddComponent<UnityEngine.Camera>();
    cam.CopyFrom(UnityEngine.Camera.main); cam.enabled = false; cam.useOcclusionCulling = false;
    cam.transform.SetPositionAndRotation(World(shot.eye), UnityEngine.Quaternion.LookRotation(World(shot.look) - World(shot.eye)));
    cam.fieldOfView = shot.fov; cam.aspect = 1.5f;
    var target = new UnityEngine.RenderTexture(1200, 800, 24);
    var previous = UnityEngine.RenderTexture.active;
    UnityEngine.Texture2D pixels = null;
    try
    {
        target.Create(); cam.targetTexture = target;
        UnityEngine.Rendering.RenderPipeline.SubmitRenderRequest(cam,
            new UnityEngine.Rendering.RenderPipeline.StandardRequest { destination = target });
        UnityEngine.RenderTexture.active = target;
        pixels = new UnityEngine.Texture2D(1200, 800, UnityEngine.TextureFormat.RGB24, false);
        pixels.ReadPixels(new UnityEngine.Rect(0, 0, 1200, 800), 0, 0); pixels.Apply();
        string path = System.IO.Path.Combine(SaveRoot.Path, shot.name + ".png");
        System.IO.File.WriteAllBytes(path, pixels.EncodeToPNG()); files.Add(path);
    }
    finally
    {
        cam.targetTexture = null; UnityEngine.RenderTexture.active = previous; target.Release();
        UnityEngine.Object.Destroy(target); if (pixels != null) UnityEngine.Object.Destroy(pixels);
        UnityEngine.Object.Destroy(go);
    }
}
return new {files};
