using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Rabies foam: little white bubbles that swell at a mouth, dribble down and pop. Used on rabid raccoons and on
/// the player when rabies symptoms kick in. Pooled, cheap, purely cosmetic. Set <see cref="rate"/> (bubbles/s,
/// 0 = off) and either parent it to a mouth or give it a <see cref="follow"/> function.
/// </summary>
public class FoamBubbles : MonoBehaviour
{
    public float rate = 4f;
    public float size = 0.06f;
    /// <summary>Optional world-space anchor provider (e.g. the player's mouth). Null = this transform.</summary>
    public System.Func<Vector3> follow;
    /// <summary>Forward direction bubbles squirt toward (null = this transform's forward).</summary>
    public System.Func<Vector3> forward;
    public bool popSounds = true;

    sealed class Bubble { public Transform t; public Vector3 vel; public float age, life, size; public bool active; }
    readonly List<Bubble> pool = new List<Bubble>();
    float accum;
    float lastPopSfx;
    Material mat;

    Vector3 Anchor => follow != null ? follow() : transform.position;
    Vector3 Fwd => forward != null ? forward() : transform.forward;

    void Update()
    {
        float dt = Time.deltaTime;
        if (dt <= 0f) return;

        var cam = Camera.main;
        bool visible = cam == null || (cam.transform.position - Anchor).sqrMagnitude < 45f * 45f;
        if (rate > 0f && visible)
        {
            accum += rate * dt;
            while (accum >= 1f) { accum -= 1f; Emit(); }
        }
        else accum = 0f;

        for (int i = 0; i < pool.Count; i++)
        {
            var b = pool[i];
            if (!b.active) continue;
            b.age += dt;
            float k = b.age / b.life;
            if (k >= 1f)
            {
                b.active = false;
                b.t.gameObject.SetActive(false);
                if (popSounds && Time.time - lastPopSfx > 0.35f && Random.value < 0.35f)
                {
                    lastPopSfx = Time.time;
                    Sfx.Play("pop", b.t.position, 0.12f, Random.Range(1.6f, 2.2f));
                }
                continue;
            }
            b.vel += Vector3.down * 1.4f * dt;
            b.vel *= Mathf.Clamp01(1f - dt * 2.2f);
            b.t.position += b.vel * dt;
            float swell = k < 0.25f ? Mathf.SmoothStep(0.2f, 1f, k / 0.25f) : 1f + Mathf.Sin(b.age * 18f) * 0.08f;
            if (k > 0.85f) swell *= 1f + (k - 0.85f) * 3f; // puff up right before popping
            b.t.localScale = Vector3.one * b.size * swell;
        }
    }

    void Emit()
    {
        Bubble b = null;
        for (int i = 0; i < pool.Count; i++) if (!pool[i].active) { b = pool[i]; break; }
        if (b == null)
        {
            if (pool.Count >= 40) return;
            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            Object.DestroyImmediate(go.GetComponent<Collider>());
            go.name = "Foam";
            go.layer = CritterWorld.CritterLayer; // lives in the active scene (dies with it)
            if (mat == null) mat = CritterLook.Mat(new Color(0.97f, 0.98f, 0.95f), 0.85f);
            var r = go.GetComponent<Renderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            b = new Bubble { t = go.transform };
            pool.Add(b);
        }
        Vector3 fwd = Fwd; fwd.y = 0f;
        fwd = fwd.sqrMagnitude > 0.001f ? fwd.normalized : Vector3.forward;
        Vector3 side = Vector3.Cross(Vector3.up, fwd);
        b.active = true;
        b.age = 0f;
        b.life = Random.Range(0.6f, 1.3f);
        b.size = size * Random.Range(0.6f, 1.4f);
        b.t.position = Anchor + side * Random.Range(-0.04f, 0.04f);
        b.vel = fwd * Random.Range(0.15f, 0.5f) + side * Random.Range(-0.25f, 0.25f) + Vector3.up * Random.Range(0.1f, 0.5f);
        b.t.localScale = Vector3.zero;
        b.t.gameObject.SetActive(true);
    }

    void OnDestroy()
    {
        foreach (var b in pool) if (b.t != null) Destroy(b.t.gameObject);
        pool.Clear();
    }
}
