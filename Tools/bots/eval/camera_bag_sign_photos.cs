// Disposable visual acceptance capture; does not write a scene or a real gameplay save.
if (!SaveRoot.IsOverridden || !UnityEngine.Application.isPlaying)
    throw new System.InvalidOperationException("Isolated play-test save required.");
void Photo(string file, UnityEngine.Vector3 eye, UnityEngine.Vector3 look, float fov)
{
    var go = new UnityEngine.GameObject("Camera/bag/sign QA photo camera");
    var cam = go.AddComponent<UnityEngine.Camera>();
    cam.CopyFrom(UnityEngine.Camera.main); cam.enabled = false; cam.useOcclusionCulling = false;
    cam.transform.SetPositionAndRotation(eye, UnityEngine.Quaternion.LookRotation(look - eye));
    cam.fieldOfView = fov; cam.aspect = 1.5f;
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
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(SaveRoot.Path, file), pixels.EncodeToPNG());
    }
    finally
    {
        cam.targetTexture = null; UnityEngine.RenderTexture.active = previous; target.Release();
        UnityEngine.Object.Destroy(target); if (pixels != null) UnityEngine.Object.Destroy(pixels);
        UnityEngine.Object.Destroy(go);
    }
}
var board = UnityEngine.GameObject.Find("[CleanupBoard]");
if (board == null) throw new System.Exception("Production Cleanup board not loaded.");
var sign = board.transform.position;
Photo("cleanup-sign-fixed.png", sign + new UnityEngine.Vector3(1f, 3.4f, 10f), sign + UnityEngine.Vector3.up * 2.9f, 42f);
var normal = UnityEngine.GameObject.Find("QA normal bag");
var heavy = UnityEngine.GameObject.Find("QA heavy bag");
if (normal != null && heavy != null)
{
    var middle = (normal.transform.position + heavy.transform.position) * .5f;
    Photo("heavy-bag-fixed.png", middle + new UnityEngine.Vector3(1f, 2.5f, 5.8f), middle + UnityEngine.Vector3.up * .9f, 48f);
}
return new { root = SaveRoot.Path, shader = board.transform.Find("Panel").GetComponent<UnityEngine.Renderer>().sharedMaterial.shader.name };
