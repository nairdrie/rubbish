using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

/// <summary>
/// A subtle day/night touch inside the garage (ShopScene), driven by <see cref="GameClock"/>: a glowing strip of
/// "outside" light leaking under the garage door, a soft spill of that light across the floor in front of it,
/// and a gentle tint on the scene's ambient. Warm white by day, orange at sunset, moon-blue at night. The
/// garage's own work lights stay untouched, so the room stays readable.
/// Placed by Setup_World on a "WorldSky" root in ShopScene; finds the door ("GarageDoor") at runtime.
/// </summary>
[DisallowMultipleComponent]
public class GarageSkyLight : MonoBehaviour
{
    [Tooltip("Name of the garage door root (its renderer bounds place the light strip).")]
    public string doorName = "GarageDoor";
    [Tooltip("Fallbacks if the door isn't found: door centre at floor level, width, and inward direction.")]
    public Vector3 doorBottomCenter = new Vector3(-6.62f, -0.1f, -2.41f);
    public float doorWidth = 4.0f;
    public Vector3 inward = Vector3.right;

    [Header("Look")]
    public float stripHeight = 0.05f;
    public float stripEmission = 2.2f;
    public float spillIntensity = 3.2f;
    public float spillRange = 7f;
    [Tooltip("How much the outdoor colour tints the garage ambient (keep small).")]
    [Range(0f, 1f)] public float ambientTint = 0.22f;

    [Header("Debug")]
    [Tooltip("Force an hour (0-24). Negative = follow GameClock.")]
    public float overrideHour = -1f;

    Transform strip;
    Material stripMat;
    Light spill;
    SphericalHarmonicsL2 baseProbe;
    bool haveBase;
    bool built;

    void Start()
    {
        if (!Application.isPlaying) return;
        Build();
        Apply(overrideHour >= 0f ? overrideHour : GameClock.Hours);
    }

    void Update()
    {
        if (!Application.isPlaying) return;
        Apply(overrideHour >= 0f ? overrideHour : GameClock.Hours);
    }

    void OnDestroy()
    {
        // (No ambient restore here: on scene change the next scene brings its own lighting, and writing the
        // garage probe now could leak into it.)
        if (stripMat != null) { if (Application.isPlaying) Destroy(stripMat); else DestroyImmediate(stripMat); }
    }

    /// <summary>Edit-mode snapshot at <paramref name="hour"/> (builds the strip/spill if needed).</summary>
    public void Preview(float hour)
    {
        Build();
        if (!haveBase) CaptureAmbient();
        Apply(hour);
    }

    /// <summary>Undo a Preview's ambient tint.</summary>
    public void EndPreview()
    {
        if (haveBase) RenderSettings.ambientProbe = baseProbe;
    }

    void CaptureAmbient()
    {
        baseProbe = RenderSettings.ambientProbe;
        haveBase = true;
    }

    void Build()
    {
        if (built && strip != null && spill != null) return;
        Vector3 bottom = doorBottomCenter;
        float width = doorWidth;
        Vector3 inDir = inward.sqrMagnitude > 0.01f ? inward.normalized : Vector3.right;

        var door = FindDoor();
        if (door != null)
        {
            var rends = door.GetComponentsInChildren<Renderer>(false);
            if (rends.Length > 0)
            {
                Bounds b = rends[0].bounds;
                foreach (var r in rends) b.Encapsulate(r.bounds);
                // Door is a thin slab: its thin axis is the in/out direction.
                bool thinX = b.size.x < b.size.z;
                width = thinX ? b.size.z : b.size.x;
                bottom = new Vector3(b.center.x, b.min.y, b.center.z);
                // inward = toward the scene origin (garage interior) along the thin axis
                Vector3 toInside = -new Vector3(b.center.x, 0f, b.center.z);
                inDir = thinX ? new Vector3(Mathf.Sign(toInside.x), 0f, 0f) : new Vector3(0f, 0f, Mathf.Sign(toInside.z));
                bottom += inDir * (thinX ? b.extents.x : b.extents.z);
            }
        }

        var stripT = transform.Find("DoorGapGlow");
        if (stripT == null)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "DoorGapGlow";
            var col = go.GetComponent<Collider>();
            if (Application.isPlaying) Destroy(col); else DestroyImmediate(col);
            if (!Application.isPlaying) go.hideFlags = HideFlags.DontSave;
            go.transform.SetParent(transform, false);
            stripT = go.transform;
        }
        strip = stripT;
        strip.position = bottom + Vector3.up * (stripHeight * 0.5f + 0.005f) + inDir * 0.01f;
        strip.rotation = Quaternion.LookRotation(inDir, Vector3.up);
        strip.localScale = new Vector3(width * 0.97f, stripHeight, 0.02f);
        var mr = strip.GetComponent<MeshRenderer>();
        mr.shadowCastingMode = ShadowCastingMode.Off;
        mr.receiveShadows = false;
        if (stripMat == null)
        {
            var sh = Shader.Find("Universal Render Pipeline/Lit");
            if (sh != null)
            {
                stripMat = new Material(sh) { name = "Sky_DoorGap", hideFlags = HideFlags.DontSave };
                stripMat.SetColor("_BaseColor", Color.black);
                stripMat.EnableKeyword("_EMISSION");
                stripMat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            }
        }
        if (stripMat != null) mr.sharedMaterial = stripMat;

        var spillT = transform.Find("DoorSpill");
        if (spillT == null)
        {
            var go = new GameObject("DoorSpill");
            if (!Application.isPlaying) go.hideFlags = HideFlags.DontSave;
            go.transform.SetParent(transform, false);
            spillT = go.transform;
        }
        spill = spillT.GetComponent<Light>();
        if (spill == null) spill = spillT.gameObject.AddComponent<Light>();
        spill.type = LightType.Spot;
        spill.shadows = LightShadows.None;
        spill.range = spillRange;
        spill.spotAngle = 160f;
        spill.innerSpotAngle = 90f;
        // Outside the door, low, aiming into the room and down: a wide fan of light pooling on the floor
        // across the whole door width (no shadows, so the closed door doesn't block it; the wall's inner face
        // points away from it and stays unlit).
        spillT.position = bottom - inDir * 1.1f + Vector3.up * 0.55f;
        spillT.rotation = Quaternion.LookRotation((inDir + Vector3.down * 0.32f).normalized, Vector3.up);
        built = true;
    }

    Transform FindDoor()
    {
        if (string.IsNullOrEmpty(doorName)) return null;
        foreach (var root in gameObject.scene.GetRootGameObjects())
        {
            if (root.name == doorName) return root.transform;
            var t = FindDeep(root.transform, doorName);
            if (t != null) return t;
        }
        return null;
    }

    static Transform FindDeep(Transform t, string name)
    {
        foreach (Transform c in t)
        {
            if (c.name == name) return c;
            var r = FindDeep(c, name);
            if (r != null) return r;
        }
        return null;
    }

    void Apply(float hour)
    {
        if (!built) Build();
        var look = SkyCycle.LookAt(hour);
        Color outside = look.doorway;
        float daylight = SkyCycle.Daylight01(hour);

        if (stripMat != null) stripMat.SetColor("_EmissionColor", outside * stripEmission * Mathf.Lerp(0.55f, 1f, daylight));
        if (spill != null)
        {
            float lum = Mathf.Max(outside.r, Mathf.Max(outside.g, outside.b));
            spill.color = lum > 0.001f ? outside / lum : Color.white;
            spill.intensity = spillIntensity * Mathf.Lerp(0.35f, 1f, daylight);
        }

        // RenderSettings belong to the active scene: only touch them while the garage is the active scene
        // (never during a transition frame), and remember the garage's own ambient the first time.
        bool active = !Application.isPlaying || SceneManager.GetActiveScene() == gameObject.scene;
        if (active && !haveBase) CaptureAmbient();
        if (active && haveBase && ambientTint > 0f)
        {
            // Gentle multiplicative tint of the baked ambient toward the outdoor colour (brighter by day).
            Color tint = Color.Lerp(Color.white, outside * Mathf.Lerp(0.8f, 1.1f, daylight), ambientTint);
            var sh = baseProbe;
            for (int c = 0; c < 9; c++)
            {
                sh[0, c] *= tint.r;
                sh[1, c] *= tint.g;
                sh[2, c] *= tint.b;
            }
            RenderSettings.ambientProbe = sh;
        }
    }
}
