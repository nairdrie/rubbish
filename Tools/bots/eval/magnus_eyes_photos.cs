// Inspect the original googly-eye anchors on every production upgrade tier, using disposable test saves only.
if (!SaveRoot.IsOverridden || !UnityEngine.Application.isPlaying)
    throw new System.InvalidOperationException("Isolated play-test save required.");
var arm = DepotRowScene.Lot(0).interior.GetComponentInChildren<MagnusArm>(true);
var visual = MagnusArmVisuals.Ensure(arm);
bool wasEnabled = visual.enabled;
visual.enabled = false;
var wrist = arm.transform.Find("BasePivot/Shoulder/Elbow/Wrist");
var files = new System.Collections.Generic.List<string>();
System.Collections.IEnumerator Capture()
{
try
{
    for (int tier = 0; tier <= 4; tier++)
    {
        visual.RebuildForLevels(tier == 0 ? 0 : tier == 4 ? 4 : 1, UnityEngine.Mathf.Clamp(tier - 1, 0, 2));
        yield return null;
        yield return null;
        var center = wrist.position + wrist.rotation * new UnityEngine.Vector3(0f, .02f, -.20f);
        var go = new UnityEngine.GameObject("Magnus eyes QA camera");
        var cam = go.AddComponent<UnityEngine.Camera>();
        cam.CopyFrom(UnityEngine.Camera.main); cam.enabled = false; cam.useOcclusionCulling = false;
        cam.transform.SetPositionAndRotation(center - wrist.right * 1.3f,
            UnityEngine.Quaternion.LookRotation(wrist.right, wrist.up));
        cam.fieldOfView = 35f; cam.aspect = 1.5f;
        var target = new UnityEngine.RenderTexture(900, 600, 24);
        var previous = UnityEngine.RenderTexture.active;
        UnityEngine.Texture2D pixels = null;
        try
        {
            target.Create(); cam.targetTexture = target;
            UnityEngine.Rendering.RenderPipeline.SubmitRenderRequest(cam,
                new UnityEngine.Rendering.RenderPipeline.StandardRequest { destination = target });
            UnityEngine.RenderTexture.active = target;
            pixels = new UnityEngine.Texture2D(900, 600, UnityEngine.TextureFormat.RGB24, false);
            pixels.ReadPixels(new UnityEngine.Rect(0, 0, 900, 600), 0, 0); pixels.Apply();
            string path = System.IO.Path.Combine(SaveRoot.Path, "magnus-eyes-" + tier + ".png");
            System.IO.File.WriteAllBytes(path, pixels.EncodeToPNG()); files.Add(path);
        }
        finally
        {
            cam.targetTexture = null; UnityEngine.RenderTexture.active = previous; target.Release();
            UnityEngine.Object.Destroy(target); if (pixels != null) UnityEngine.Object.Destroy(pixels);
            UnityEngine.Object.Destroy(go);
        }
    }
}
finally
{
    visual.RebuildForLevels(DepotMachineContext.Level(arm, ShopIds.MagnusBrain),
        DepotMachineContext.Level(arm, ShopIds.MagnusGripper));
    visual.enabled = wasEnabled;
}
UnityEngine.Debug.Log("[QA] Magnus eye captures complete: " + string.Join(", ", files));
}
arm.StartCoroutine(Capture());
return "Capturing five tiers on separate frames; files magnus-eyes-0.png through magnus-eyes-4.png in isolated SaveRoot.";
