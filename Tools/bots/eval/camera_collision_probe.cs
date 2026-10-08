// Camera/room regression probe. Only run in disposable Play mode; restores the local camera/player pose.
if (!SaveRoot.IsOverridden || !UnityEngine.Application.isPlaying)
    throw new System.InvalidOperationException("Isolated play-test save required.");
var player = PlayerController.Local;
var cam = player.playerCamVCam;
var origin = WorkshopWorld.Origin(0);
var oldPosition = player.transform.position;
var oldRotation = cam.Follow.rotation;
int tested = 0;
var failures = new System.Collections.Generic.List<object>();
try
{
    // garage-local spots where a player stands at the decor (WorkshopWorld.PlacementFor): the display line on the south
    // wall, the wall display, the break corner by the office, and the laptop
    foreach (var local in new[] { new UnityEngine.Vector3(-1f, 0f, -3.9f), new UnityEngine.Vector3(1.35f, 0f, -4.1f),
        new UnityEngine.Vector3(5.2f, 0f, -.1f), new UnityEngine.Vector3(5.6f, .3f, 1.6f) })
    foreach (float pitch in new[] { -20f, 10f, 45f })
    for (int yaw = 0; yaw < 360; yaw += 45)
    {
        player.transform.position = origin + local;
        cam.Follow.rotation = UnityEngine.Quaternion.Euler(pitch, yaw, 0f);
        UnityEngine.Physics.SyncTransforms();
        cam.PreviousStateIsValid = false;
        cam.InternalUpdateCameraState(UnityEngine.Vector3.up, -1f);
        var state = cam.State;
        var near = state.Lens.NearClipPlane;
        float h = UnityEngine.Mathf.Tan(state.Lens.FieldOfView * UnityEngine.Mathf.Deg2Rad * .5f) * near;
        float w = h * UnityEngine.Camera.main.aspect;
        tested++;
        foreach (float x in new[] { -w, w }) foreach (float y in new[] { -h, h })
        {
            var corner = state.FinalPosition + state.FinalOrientation * new UnityEngine.Vector3(x, y, near);
            if (UnityEngine.Physics.Linecast(cam.LookAt.position, corner, out var hit, 1 << SortingKit.WallsLayer,
                UnityEngine.QueryTriggerInteraction.Ignore))
                failures.Add(new { local = local.ToString("F2"), pitch, yaw, wall = hit.collider.name,
                    camera = (state.FinalPosition - origin).ToString("F3"), corner = (corner - origin).ToString("F3") });
        }
    }
}
finally
{
    player.transform.position = oldPosition;
    cam.Follow.rotation = oldRotation;
    cam.PreviousStateIsValid = false;
    UnityEngine.Physics.SyncTransforms();
}
return new { tested, failures };
