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
var origin = WorkshopWorld.Origin(lot);
// garage-local views of the decor slots (WorkshopWorld.PlacementFor): the display line on the south wall, the break
// corner by the office, and the whole room from the garage door
Photo("workshop-displays.png", origin + new UnityEngine.Vector3(.2f, 2.4f, 1.6f), origin + new UnityEngine.Vector3(.6f, .9f, -4.6f), 70f);
Photo("workshop-break-corner.png", origin + new UnityEngine.Vector3(1.2f, 2.3f, -.9f), origin + new UnityEngine.Vector3(6.2f, .9f, -.3f), 70f);
Photo("workshop-room.png", origin + new UnityEngine.Vector3(-6.2f, 3.4f, -.4f), origin + new UnityEngine.Vector3(2.5f, .6f, -.6f), 78f);
// the forecourt (door-local): sign and doorbell by the door, hoop and launcher, and the Monument on the other side
int streetLot = UnityEngine.Mathf.Max(0, lot);
if (DepotRowWorld.Active || DepotRowWorld.LegacyStreet)
{
    Photo("workshop-forecourt.png", DepotRowLayout.StreetPoint(streetLot, new UnityEngine.Vector3(0f, 5f, 26f)), DepotRowLayout.StreetPoint(streetLot, new UnityEngine.Vector3(0f, 2.5f, 0f)), 62f);
    Photo("workshop-hoop.png", DepotRowLayout.StreetPoint(streetLot, new UnityEngine.Vector3(3f, 3f, 16f)), DepotRowLayout.StreetPoint(streetLot, new UnityEngine.Vector3(10.5f, 1.4f, 4f)), 60f);
    var m = WorkshopWorld.MonumentLocal;
    Photo("workshop-monument.png", DepotRowLayout.StreetPoint(streetLot, new UnityEngine.Vector3(m.x + 3f, 2.2f, m.z + 7f)), DepotRowLayout.StreetPoint(streetLot, new UnityEngine.Vector3(m.x, 1.4f, m.z)), 50f);
}
return new { root = SaveRoot.Path, lot };
