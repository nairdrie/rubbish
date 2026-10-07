using UnityEngine;

/// <summary>
/// Cartoon dizzy stars orbiting a head for a few seconds (stunned critters, bonked players). Self-destroys.
/// Use <see cref="Attach"/>; calling it again on the same target just extends the time.
/// </summary>
public class DizzyStars : MonoBehaviour
{
    Transform[] stars;
    float until;
    float radius = 0.28f;
    float spin;

    public static DizzyStars Attach(Transform head, float seconds, float radius = 0.28f, float height = 0.22f)
    {
        if (head == null) return null;
        var existing = head.GetComponentInChildren<DizzyStars>();
        if (existing != null)
        {
            existing.until = Mathf.Max(existing.until, Time.time + seconds);
            return existing;
        }
        var go = new GameObject("DizzyStars");
        go.layer = CritterWorld.CritterLayer;
        go.transform.SetParent(head, false);
        go.transform.localPosition = new Vector3(0f, height, 0f);
        var d = go.AddComponent<DizzyStars>();
        d.radius = radius;
        d.until = Time.time + seconds;
        d.Build();
        return d;
    }

    void Build()
    {
        stars = new Transform[4];
        var gold = CritterLook.Unlit(new Color(1f, 0.88f, 0.2f));
        for (int i = 0; i < stars.Length; i++)
        {
            var s = new GameObject("Star").transform;
            s.SetParent(transform, false);
            // A chunky 3D "star": two cubes rotated 45 degrees apart.
            for (int k = 0; k < 2; k++)
            {
                var c = GameObject.CreatePrimitive(PrimitiveType.Cube);
                var collider = c.GetComponent<Collider>();
                // Bonks also arrive during physics callbacks. Disable immediately, remove safely at frame end.
                collider.enabled = false;
                if (Application.isPlaying) Object.Destroy(collider);
                else Object.DestroyImmediate(collider);
                c.layer = CritterWorld.CritterLayer;
                c.transform.SetParent(s, false);
                c.transform.localScale = new Vector3(0.07f, 0.07f, 0.03f);
                c.transform.localRotation = Quaternion.Euler(0f, 0f, k * 45f);
                var r = c.GetComponent<Renderer>();
                r.sharedMaterial = gold;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
            stars[i] = s;
        }
    }

    void Update()
    {
        if (Time.time >= until)
        {
            Juice.ShrinkAndDestroy(gameObject, 0.2f);
            enabled = false;
            return;
        }
        spin += Time.deltaTime * 320f;
        // Undo parent scale so the stars keep their size on squashed / scaled heads.
        Vector3 ps = transform.parent != null ? transform.parent.lossyScale : Vector3.one;
        transform.localScale = new Vector3(1f / Mathf.Max(0.01f, ps.x), 1f / Mathf.Max(0.01f, ps.y), 1f / Mathf.Max(0.01f, ps.z));
        transform.rotation = Quaternion.identity;
        for (int i = 0; i < stars.Length; i++)
        {
            if (stars[i] == null) continue;
            float a = (spin + i * 360f / stars.Length) * Mathf.Deg2Rad;
            stars[i].localPosition = new Vector3(Mathf.Cos(a) * radius, Mathf.Sin(a * 2f) * 0.04f, Mathf.Sin(a) * radius);
            stars[i].localRotation = Quaternion.Euler(0f, spin * 2f, spin * 3f + i * 20f);
        }
    }
}
