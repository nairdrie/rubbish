// Read-only production geometry photos in an isolated runtime. No scene or assets are saved.
if (!SaveRoot.IsOverridden || !UnityEngine.Application.isPlaying)
    throw new System.InvalidOperationException("Isolated play-test save required.");
void Photo(string file, UnityEngine.Vector3 eye, UnityEngine.Vector3 look, float fov)
{
    var go = new UnityEngine.GameObject("Workshop QA photo camera");
    var cam = go.AddComponent<UnityEngine.Camera>();
    cam.CopyFrom(UnityEngine.Camera.main); cam.enabled = false; cam.useOcclusionCulling = false;
    cam.transform.SetPositionAndRotation(eye, UnityEngine.Quaternion.LookRotation(look - eye));
    cam.fieldOfView = fov; cam.aspect = 1.5f;
    var target = new UnityEngine.RenderTexture(1200, 800, 24);
    var previous = UnityEngine.RenderTexture.active;
    UnityEngine.Texture2D pixels = null;
    try {
        target.Create(); cam.targetTexture = target;
        UnityEngine.Rendering.RenderPipeline.SubmitRenderRequest(cam,
            new UnityEngine.Rendering.RenderPipeline.StandardRequest { destination = target });
        UnityEngine.RenderTexture.active = target;
        pixels = new UnityEngine.Texture2D(1200, 800, UnityEngine.TextureFormat.RGB24, false);
        pixels.ReadPixels(new UnityEngine.Rect(0, 0, 1200, 800), 0, 0); pixels.Apply();
        System.IO.File.WriteAllBytes(System.IO.Path.Combine(SaveRoot.Path, file), pixels.EncodeToPNG());
    }
    finally {
        cam.targetTexture = null; UnityEngine.RenderTexture.active = previous; target.Release();
        UnityEngine.Object.Destroy(target); if (pixels != null) UnityEngine.Object.Destroy(pixels);
        UnityEngine.Object.Destroy(go);
    }
}
int lot = DepotRowWorld.Active ? DepotRowLayout.InteriorAt(PlayerController.Local.transform.position) : -1;
var origin = WorkshopAnnex.Origin(lot);
Photo("workshop-room.png", origin + new UnityEngine.Vector3(7.4f, 2.35f, -3.8f), origin + new UnityEngine.Vector3(10.8f, 1.05f, 1.5f), 80f);
Photo("workshop-portal.png", origin + new UnityEngine.Vector3(2.6f, 2.05f, -.25f), origin + new UnityEngine.Vector3(9.3f, 1.1f, -.25f), 72f);
Photo("workshop-bench.png", origin + new UnityEngine.Vector3(9.85f, 1.6f, .95f), origin + new UnityEngine.Vector3(12.45f, 1.05f, 1.6f), 72f);
return new { root = SaveRoot.Path, lot };
