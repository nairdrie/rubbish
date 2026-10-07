using UnityEngine;

[DefaultExecutionOrder(-1000)]
public class BlackoutUntilLocalPlayer : MonoBehaviour
{
    Camera cam;
    int savedMask;
    CameraClearFlags savedClear;
    Color savedBg;

    void Awake()
    {
        cam = GetComponent<Camera>();
        savedMask = cam.cullingMask;
        savedClear = cam.clearFlags;
        savedBg = cam.backgroundColor;

        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = Color.black;
        cam.cullingMask = 0;                 // render nothing => black
    }

    public void Unlock()
    {
        if (!cam) cam = GetComponent<Camera>();
        cam.cullingMask = savedMask;
        cam.clearFlags = savedClear;
        cam.backgroundColor = savedBg;
    }
}