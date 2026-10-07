using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Snoring Z's: little blocky "Z" letters (three thin bars each) that puff out of a sleeping critter's head,
/// drift up and sideways, grow, wobble and shrink away. Always face the camera. Set <see cref="Emitting"/>.
/// </summary>
public class CritterZs : MonoBehaviour
{
    public bool Emitting = true;
    public float interval = 1.1f;
    public Color color = new Color(0.65f, 0.85f, 1f);

    sealed class Z { public Transform t; public float age, life, size, sway; public Vector3 start; }
    readonly List<Z> live = new List<Z>();
    float nextAt;
    int count;

    void Update()
    {
        float dt = Time.deltaTime;
        var cam = Camera.main;
        if (Emitting && Time.time >= nextAt)
        {
            nextAt = Time.time + interval * Random.Range(0.8f, 1.25f);
            // Nobody around to see them? Don't bother.
            if (cam != null && (cam.transform.position - transform.position).sqrMagnitude < 55f * 55f) Spawn();
        }

        for (int i = live.Count - 1; i >= 0; i--)
        {
            var z = live[i];
            if (z.t == null) { live.RemoveAt(i); continue; }
            z.age += dt;
            float k = z.age / z.life;
            if (k >= 1f) { Destroy(z.t.gameObject); live.RemoveAt(i); continue; }
            float grow = k < 0.2f ? Mathf.SmoothStep(0f, 1f, k / 0.2f) : k > 0.75f ? Mathf.SmoothStep(1f, 0f, (k - 0.75f) / 0.25f) : 1f;
            z.t.position = z.start + new Vector3(Mathf.Sin(z.age * 2.2f + z.sway) * 0.18f + z.age * 0.12f, z.age * 0.45f, 0f);
            z.t.localScale = Vector3.one * z.size * (0.6f + 0.6f * k) * grow;
            if (cam != null)
                z.t.rotation = Quaternion.LookRotation(z.t.position - cam.transform.position, Vector3.up) * Quaternion.Euler(0f, 0f, Mathf.Sin(z.age * 3f + z.sway) * 14f);
        }
    }

    void Spawn()
    {
        var root = new GameObject("Z");
        root.transform.SetParent(null, false);
        root.layer = CritterWorld.CritterLayer;
        var mat = CritterLook.Unlit(color);
        // "Z" from three bars: top, diagonal, bottom (in a 1x1 box).
        Bar(root.transform, mat, new Vector3(0f, 0.42f, 0f), new Vector3(0.8f, 0.16f, 0.12f), 0f);
        Bar(root.transform, mat, Vector3.zero, new Vector3(1.02f, 0.15f, 0.12f), 45f);
        Bar(root.transform, mat, new Vector3(0f, -0.42f, 0f), new Vector3(0.8f, 0.16f, 0.12f), 0f);
        count++;
        var z = new Z
        {
            t = root.transform,
            life = Random.Range(1.8f, 2.4f),
            size = (count % 3 == 0 ? 0.26f : 0.17f) * Random.Range(0.9f, 1.1f),
            sway = Random.Range(0f, 6f),
            start = transform.position,
        };
        root.transform.position = z.start;
        root.transform.localScale = Vector3.zero;
        live.Add(z);
    }

    static void Bar(Transform parent, Material mat, Vector3 pos, Vector3 scale, float rollDeg)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        Object.DestroyImmediate(go.GetComponent<Collider>());
        go.layer = CritterWorld.CritterLayer;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos;
        go.transform.localRotation = Quaternion.Euler(0f, 0f, rollDeg);
        go.transform.localScale = scale;
        var r = go.GetComponent<Renderer>();
        r.sharedMaterial = mat;
        r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
    }

    void OnDestroy()
    {
        foreach (var z in live) if (z.t != null) Destroy(z.t.gameObject);
        live.Clear();
    }
}
