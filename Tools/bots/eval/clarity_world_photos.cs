// Actual production objects/materials, under a disposable play-test save. No scene/assets are saved.
if (!SaveRoot.IsOverridden || !UnityEngine.Application.isPlaying)
    throw new System.InvalidOperationException("Isolated play-test save required.");
void Photo(string file, UnityEngine.Vector3 eye, UnityEngine.Vector3 look, float fov)
{
    var go = new UnityEngine.GameObject("Clarity QA photo camera");
    var cam = go.AddComponent<UnityEngine.Camera>();
    cam.CopyFrom(UnityEngine.Camera.main);
    cam.enabled = false;
    cam.useOcclusionCulling = false;
    cam.transform.SetPositionAndRotation(eye, UnityEngine.Quaternion.LookRotation(look-eye));
    cam.fieldOfView = fov;
    cam.aspect = 1.5f;
    var target = new UnityEngine.RenderTexture(1200, 800, 24);
    var previous = UnityEngine.RenderTexture.active;
    UnityEngine.Texture2D pixels = null;
    try
    {
        target.Create(); cam.targetTexture = target;
        var request = new UnityEngine.Rendering.RenderPipeline.StandardRequest {destination=target};
        UnityEngine.Rendering.RenderPipeline.SubmitRenderRequest(cam, request);
        UnityEngine.RenderTexture.active = target;
        pixels = new UnityEngine.Texture2D(1200,800,UnityEngine.TextureFormat.RGB24,false);
        pixels.ReadPixels(new UnityEngine.Rect(0,0,1200,800),0,0);pixels.Apply();
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(SaveRoot.Path,file),pixels.EncodeToPNG());
    }
    finally
    {
        cam.targetTexture=null;UnityEngine.RenderTexture.active=previous;
        target.Release();UnityEngine.Object.Destroy(target);
        if(pixels!=null)UnityEngine.Object.Destroy(pixels);
        UnityEngine.Object.Destroy(go);
    }
}
var board = UnityEngine.GameObject.Find("[CleanupBoard]");
if(board!=null)
{
    var p=board.transform.position;
    Photo("cleanup-sign.png",p+new UnityEngine.Vector3(0,3.2f,10f),p+UnityEngine.Vector3.up*2.8f,40f);
}
// The first-aid shelf by the laptop (RabiesShotKiosk) was removed: nothing to photograph there any more.
return new {root=SaveRoot.Path,board=board!=null,kiosk=false};
