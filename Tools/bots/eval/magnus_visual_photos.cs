// Disposable captures of production joint-mounted models. Never changes upgrades, scenes or real saves.
if (!SaveRoot.IsOverridden || !UnityEngine.Application.isPlaying)
    throw new System.InvalidOperationException("Isolated play-test save required.");
var arm = UnityEngine.Object.FindObjectsByType<MagnusArm>(UnityEngine.FindObjectsInactive.Include,
    UnityEngine.FindObjectsSortMode.None).OrderBy(x => DepotMachineContext.LotOf(x)).First(x => DepotMachineContext.LotOf(x) >= 0);
var vis = MagnusArmVisuals.Ensure(arm); vis.enabled = false;
var photos = new System.Collections.Generic.List<string>();
var info = new System.Collections.Generic.List<object>();
var wrist = arm.transform.Find("BasePivot/Shoulder/Elbow/Wrist");
foreach (var eyes in arm.GetComponentsInChildren<GooglyEye>(true))
    info.Add(new { eye = eyes.name, wristMetres = (UnityEngine.Quaternion.Inverse(wrist.rotation) *
        (eyes.transform.position - wrist.position)).ToString("F4") });
for (int tier = 0; tier <= 4; tier++)
{
    vis.RebuildForLevels(tier == 0 ? 0 : tier == 4 ? 4 : 1, UnityEngine.Mathf.Clamp(tier - 1, 0, 2));
    var look = arm.transform.position + UnityEngine.Vector3.up * 1.6f;
    var face = UnityEngine.Vector3.ProjectOnPlane(-wrist.right, UnityEngine.Vector3.up).normalized;
    var eye = look + face * 3.2f + UnityEngine.Vector3.Cross(UnityEngine.Vector3.up, face) * .8f + UnityEngine.Vector3.up * .4f;
    var go = new UnityEngine.GameObject("Magnus QA camera");
    var cam = go.AddComponent<UnityEngine.Camera>();
    cam.CopyFrom(UnityEngine.Camera.main); cam.enabled = false; cam.useOcclusionCulling = false;
    cam.transform.SetPositionAndRotation(eye, UnityEngine.Quaternion.LookRotation(look - eye));
    cam.fieldOfView = 59f; cam.aspect = 1.5f;
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
        string path = System.IO.Path.Combine(SaveRoot.Path, "magnus-tier-" + tier + ".png");
        System.IO.File.WriteAllBytes(path, pixels.EncodeToPNG()); photos.Add(path);
    }
    finally
    {
        cam.targetTexture = null; UnityEngine.RenderTexture.active = previous; target.Release();
        UnityEngine.Object.Destroy(target); if (pixels != null) UnityEngine.Object.Destroy(pixels);
        UnityEngine.Object.Destroy(go);
    }
}
return new { photos, eyes = info, arm = arm.transform.position.ToString("F3") };
