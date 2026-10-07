using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Pooled mesh particles (tiny cubes, confetti slabs, spheres) driven by our own update instead of
/// ParticleSystem, so there are no URP particle-shader headaches. Colors go through a MaterialPropertyBlock
/// (_BaseColor and _Color). Materials: RubbishAssets "mat/fx_unlit" / "mat/fx_additive" when Setup_Feel has
/// registered them, else a copy of the default primitive material. Live particles are capped at MaxParticles;
/// when full the oldest short-lived particle is recycled.
/// </summary>
public sealed class FeelFx
{
    public const int MaxParticles = 600;
    const int MaxSparkleEmitters = 48;
    const int MaxStinkEmitters = 24;
    const float StinkLodDistance = 35f;
    const float Gravity = 14f;

    enum Kind { Debris, Dust, Twinkle, StinkLine, Fly }
    enum Shape { Cube, Slab, Sphere }

    struct P
    {
        public bool alive;
        public Kind kind;
        public Vector3 pos, vel, spinAxis, baseScale, local;
        public Quaternion rot;
        public float spin, life, maxLife, groundY, angle, angVel, phase;
        public Color color;
        public Emitter emitter;
        public bool additive;
    }

    sealed class Emitter
    {
        public bool stink;
        public Transform target;
        public Color color;
        public float start, end, acc, radius, height, nextBounds, nextLine;
        public Vector3 center, centerOffset;
        public readonly List<Line> lines = new List<Line>();
        public bool dead;
    }

    struct Line { public Vector3 offset; public float phase, nextSeg; public int segsLeft; }

    readonly Transform root;
    readonly P[] ps = new P[MaxParticles];
    readonly Transform[] tr = new Transform[MaxParticles];
    readonly MeshRenderer[] mr = new MeshRenderer[MaxParticles];
    readonly MeshFilter[] mf = new MeshFilter[MaxParticles];
    readonly Stack<int> free = new Stack<int>();
    int created, stealCursor, liveCount;
    readonly List<Emitter> emitters = new List<Emitter>();
    float clock; // scaled time advanced by Tick (slows down during hit stop)
    readonly MaterialPropertyBlock mpb = new MaterialPropertyBlock();
    static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    static readonly int ColorId = Shader.PropertyToID("_Color");

    Material opaqueMat, additiveMat;
    Mesh cubeMesh, sphereMesh;
    readonly List<Material> ownedMaterials = new List<Material>();
    static readonly List<Renderer> rendererBuffer = new List<Renderer>();

    public int LiveParticles => liveCount;

    public FeelFx(Transform parent)
    {
        root = new GameObject("Fx").transform;
        root.SetParent(parent, false);
    }

    // ------------------------------------------------------------------ public API

    public void Burst(Vector3 pos, Color color, int count, float speed)
    {
        count = Mathf.Clamp(count, 1, 120);
        speed = Mathf.Clamp(speed, 0f, 30f);
        float groundY = GroundBelow(pos);
        for (int i = 0; i < count; i++)
        {
            int k = Alloc(); if (k < 0) return;
            var dir = Random.onUnitSphere;
            dir.y = Mathf.Abs(dir.y) * 1.2f + 0.25f;
            dir.Normalize();
            bool slab = Random.value < 0.6f;
            float s = Random.Range(0.7f, 1.3f);
            ref var p = ref ps[k];
            p.kind = Kind.Debris;
            p.pos = pos + Random.insideUnitSphere * 0.08f;
            p.vel = dir * speed * Random.Range(0.45f, 1.1f);
            p.rot = Random.rotation;
            p.spinAxis = Random.onUnitSphere;
            p.spin = Random.Range(200f, 720f);
            p.baseScale = slab ? new Vector3(0.11f, 0.11f, 0.022f) * s : Vector3.one * Random.Range(0.06f, 0.11f) * s;
            p.maxLife = p.life = Random.Range(0.7f, 1.2f);
            p.groundY = groundY;
            p.color = Jitter(color);
            Activate(k, slab ? Shape.Slab : Shape.Cube, false);
        }
    }

    public void Dust(Vector3 pos, float size)
    {
        size = Mathf.Clamp(size, 0.1f, 5f);
        int n = Mathf.Clamp(Mathf.RoundToInt(8 + 6 * size), 4, 30);
        for (int i = 0; i < n; i++)
        {
            int k = Alloc(); if (k < 0) return;
            var d2 = Random.insideUnitCircle.normalized;
            var outward = new Vector3(d2.x, 0f, d2.y);
            float g = Random.Range(0.55f, 0.78f);
            ref var p = ref ps[k];
            p.kind = Kind.Dust;
            p.pos = pos + outward * Random.Range(0.05f, 0.25f) * size + Vector3.up * 0.05f;
            p.vel = outward * Random.Range(1.2f, 2.5f) * Mathf.Sqrt(size) + Vector3.up * Random.Range(0.3f, 0.9f);
            p.rot = Random.rotation;
            p.spinAxis = Random.onUnitSphere;
            p.spin = Random.Range(20f, 90f);
            p.baseScale = Vector3.one * Random.Range(0.18f, 0.35f) * size;
            p.maxLife = p.life = Random.Range(0.5f, 0.9f);
            p.color = new Color(g, g * 0.97f, g * 0.92f);
            Activate(k, Shape.Sphere, false);
        }
    }

    /// <summary>
    /// Twinkles orbiting target until duration runs out or target is destroyed (a huge duration = "while it
    /// exists"). A second call on the same target refreshes/extends it instead of stacking emitters.
    /// </summary>
    public void Sparkle(Transform target, Color color, float duration)
    {
        float end = clock + Mathf.Max(0.05f, duration);
        foreach (var existing in emitters)
        {
            if (existing.dead || existing.stink || existing.target != target) continue;
            existing.end = Mathf.Max(existing.end, end);
            existing.color = color;
            existing.start = clock; // back to the busy rate for a moment
            for (int i = 0; i < 6; i++) SpawnTwinkle(existing);
            return;
        }

        // keep the number of sparkle emitters sane: retire the oldest if there are too many
        int sparkles = 0;
        Emitter oldest = null;
        foreach (var existing in emitters)
        {
            if (existing.dead || existing.stink) continue;
            sparkles++;
            if (oldest == null || existing.start < oldest.start) oldest = existing;
        }
        if (sparkles >= MaxSparkleEmitters && oldest != null) oldest.end = clock;

        var e = new Emitter { stink = false, target = target, color = color, start = clock, end = end };
        RefreshBounds(e);
        emitters.Add(e);
        for (int i = 0; i < 6; i++) SpawnTwinkle(e); // instant pop of sparkles
    }

    public void Stink(Transform target)
    {
        foreach (var existing in emitters)
            if (!existing.dead && existing.stink && existing.target == target) return;
        int stinkCount = 0;
        foreach (var existing in emitters) if (existing.stink && !existing.dead) stinkCount++;
        if (stinkCount >= MaxStinkEmitters) return;

        var e = new Emitter { stink = true, target = target, start = clock, end = float.PositiveInfinity };
        RefreshBounds(e);
        emitters.Add(e);
        for (int i = 0; i < 3; i++)
        {
            int k = Alloc(); if (k < 0) break;
            ref var p = ref ps[k];
            p.kind = Kind.Fly;
            p.emitter = e;
            p.angle = Random.Range(0f, Mathf.PI * 2f);
            p.angVel = Random.Range(2.5f, 5f) * (Random.value < 0.5f ? -1f : 1f);
            p.phase = Random.Range(0f, 100f);
            p.baseScale = Vector3.one * 0.035f;
            p.maxLife = p.life = float.PositiveInfinity;
            p.color = new Color(0.05f, 0.05f, 0.05f);
            p.rot = Quaternion.identity;
            p.pos = e.center;
            Activate(k, Shape.Sphere, false);
        }
    }

    /// <summary>Kill every particle and emitter (scene change).</summary>
    public void ClearAll()
    {
        for (int k = 0; k < created; k++) if (ps[k].alive) Kill(k);
        emitters.Clear();
    }

    public void Dispose()
    {
        foreach (var m in ownedMaterials) FeelRunner.SafeDestroy(m);
        if (root != null) FeelRunner.SafeDestroy(root.gameObject);
        ownedMaterials.Clear();
    }

    // ------------------------------------------------------------------ update

    public void Tick(float dt)
    {
        clock += Mathf.Max(0f, dt);
        float time = clock;
        TickEmitters(dt, time);
        if (liveCount == 0) return;

        for (int k = 0; k < created; k++)
        {
            ref var p = ref ps[k];
            if (!p.alive) continue;
            p.life -= dt;
            if (p.life <= 0f) { Kill(k); continue; }
            float age01 = p.maxLife > 0f && !float.IsInfinity(p.maxLife) ? 1f - p.life / p.maxLife : 0f;
            float scale = 1f;

            switch (p.kind)
            {
                case Kind.Debris:
                {
                    p.vel.y -= Gravity * dt;
                    p.pos += p.vel * dt;
                    float half = p.baseScale.x * 0.5f;
                    if (p.pos.y < p.groundY + half && p.vel.y < 0f)
                    {
                        p.pos.y = p.groundY + half;
                        p.vel.y *= -0.35f;
                        p.vel.x *= 0.6f; p.vel.z *= 0.6f;
                        p.spin *= 0.6f;
                    }
                    p.rot = Quaternion.AngleAxis(p.spin * dt, p.spinAxis) * p.rot;
                    scale = PopShrink(age01, 0.08f, 0.3f);
                    break;
                }
                case Kind.Dust:
                {
                    p.vel *= Mathf.Exp(-4f * dt);
                    p.vel.y += 0.2f * dt;
                    p.pos += p.vel * dt;
                    p.rot = Quaternion.AngleAxis(p.spin * dt, p.spinAxis) * p.rot;
                    scale = age01 < 0.25f ? Mathf.Lerp(0.4f, 1f, age01 / 0.25f) : 1f - EaseIn((age01 - 0.25f) / 0.75f);
                    break;
                }
                case Kind.Twinkle:
                {
                    var e = p.emitter;
                    bool attached = e != null && !e.dead && e.target != null;
                    p.angle += p.angVel * dt;
                    p.local.y += 0.2f * dt;
                    if (attached)
                    {
                        float r = p.local.x;
                        p.pos = e.center + new Vector3(Mathf.Cos(p.angle) * r, p.local.y, Mathf.Sin(p.angle) * r);
                    }
                    p.rot = Quaternion.AngleAxis(p.spin * dt, Vector3.up) * p.rot;
                    scale = Mathf.Pow(Mathf.Sin(Mathf.PI * age01), 0.7f) * (age01 < 0.2f ? 1.2f : 1f);
                    if (p.additive) SetColor(k, p.color * scale);
                    break;
                }
                case Kind.StinkLine:
                {
                    var e = p.emitter;
                    if (e != null && !e.dead && e.target != null)
                    {
                        float h = p.local.y + 0.45f * (p.maxLife - p.life);
                        float x = 0.06f * Mathf.Sin(2f * Mathf.PI * h / 0.35f + p.phase + 2f * time);
                        p.pos = e.center + Vector3.up * e.height * 0.5f + new Vector3(p.local.x + x, h, p.local.z);
                    }
                    else p.pos += Vector3.up * 0.45f * dt;
                    scale = PopShrink(age01, 0.15f, 0.35f);
                    break;
                }
                case Kind.Fly:
                {
                    var e = p.emitter;
                    if (e == null || e.dead || e.target == null)
                    {
                        // target gone: buzz off
                        if (float.IsInfinity(p.maxLife)) { p.maxLife = p.life = 0.6f; p.vel = Random.onUnitSphere * 3f + Vector3.up * 2f; }
                        p.pos += p.vel * dt;
                        break;
                    }
                    float n = Mathf.PerlinNoise(p.phase, time * 0.7f);
                    p.angle += p.angVel * (0.5f + n) * dt;
                    float r = Mathf.Max(0.25f, e.radius * 0.8f) * (0.7f + 0.6f * Mathf.PerlinNoise(p.phase + 13f, time * 0.9f));
                    float y = e.height * 0.5f + 0.2f + 0.3f * Mathf.PerlinNoise(p.phase + 31f, time * 1.3f);
                    p.pos = e.center + new Vector3(Mathf.Cos(p.angle) * r, y, Mathf.Sin(p.angle) * r) + Random.insideUnitSphere * 0.012f;
                    break;
                }
            }

            var t = tr[k];
            t.SetPositionAndRotation(p.pos, p.rot);
            t.localScale = p.baseScale * Mathf.Max(0f, scale);
        }
    }

    void TickEmitters(float dt, float time)
    {
        if (emitters.Count == 0) return;
        var cam = Camera.main;
        Vector3? camPos = cam != null ? cam.transform.position : (Vector3?)null;
        for (int i = emitters.Count - 1; i >= 0; i--)
        {
            var e = emitters[i];
            if (e.target == null || time >= e.end)
            {
                e.dead = true;
                emitters.RemoveAt(i);
                continue;
            }
            if (time >= e.nextBounds) RefreshBounds(e);
            else e.center = e.target.position + e.centerOffset; // follow the target between bounds refreshes

            if (!e.stink)
            {
                // busy for the first few seconds, then a lazy idle glint for long-lived sparkles
                e.acc += dt * (time - e.start < 3f ? 16f : 6f);
                while (e.acc >= 1f) { e.acc -= 1f; SpawnTwinkle(e); }
                continue;
            }

            // stink: start a wavy line every ~0.5-0.9 s (not when far from the camera); each line lays down
            // segments that rise together
            if (time >= e.nextLine)
            {
                e.nextLine = time + Random.Range(0.5f, 0.9f);
                bool far = camPos.HasValue && (e.center - camPos.Value).sqrMagnitude > StinkLodDistance * StinkLodDistance;
                if (!far)
                {
                    var d = Random.insideUnitCircle * Mathf.Max(0.15f, e.radius * 0.6f);
                    e.lines.Add(new Line { offset = new Vector3(d.x, 0f, d.y), phase = Random.Range(0f, 6.28f), nextSeg = time, segsLeft = 9 });
                }
            }
            for (int l = e.lines.Count - 1; l >= 0; l--)
            {
                var line = e.lines[l];
                while (line.segsLeft > 0 && time >= line.nextSeg)
                {
                    line.segsLeft--;
                    line.nextSeg += 0.045f;
                    int k = Alloc(); if (k < 0) break;
                    ref var p = ref ps[k];
                    p.kind = Kind.StinkLine;
                    p.emitter = e;
                    p.local = line.offset;
                    p.phase = line.phase;
                    p.maxLife = p.life = 1.0f;
                    p.baseScale = new Vector3(0.045f, 0.09f, 0.045f);
                    p.rot = Quaternion.identity;
                    p.pos = e.center;
                    float g = Random.Range(0.85f, 1.1f);
                    p.color = new Color(0.5f * g, 0.9f * g, 0.2f * g);
                    Activate(k, Shape.Cube, false);
                }
                if (line.segsLeft <= 0) e.lines.RemoveAt(l);
                else e.lines[l] = line;
            }
        }
    }

    void SpawnTwinkle(Emitter e)
    {
        int k = Alloc(); if (k < 0) return;
        ref var p = ref ps[k];
        p.kind = Kind.Twinkle;
        p.emitter = e;
        p.angle = Random.Range(0f, Mathf.PI * 2f);
        p.angVel = Random.Range(1.5f, 3f) * (Random.value < 0.5f ? -1f : 1f);
        float r = Mathf.Max(0.12f, e.radius) * Random.Range(0.6f, 1.25f);
        p.local = new Vector3(r, Random.Range(-0.3f, 1f) * Mathf.Max(0.15f, e.height * 0.6f), 0f);
        p.pos = e.center + new Vector3(Mathf.Cos(p.angle) * r, p.local.y, Mathf.Sin(p.angle) * r);
        p.rot = Quaternion.Euler(45f, Random.Range(0f, 90f), 45f);
        p.spin = Random.Range(90f, 360f);
        // mostly solid bright diamonds (read on any background) plus some bigger additive glints
        bool glint = additiveMat != null && Random.value < 0.35f;
        p.baseScale = Vector3.one * Random.Range(0.06f, 0.11f) * (glint ? 1.5f : 1f) * Mathf.Clamp(e.radius * 2.5f, 0.8f, 2f);
        p.maxLife = p.life = Random.Range(0.45f, 0.7f);
        p.color = Color.Lerp(e.color, Color.white, Random.Range(0f, 0.6f));
        Activate(k, Shape.Cube, glint);
    }

    // ------------------------------------------------------------------ pool

    int Alloc()
    {
        if (free.Count > 0) return free.Pop();
        if (created < MaxParticles)
        {
            if (!EnsureResources()) return -1;
            int k = created++;
            var go = new GameObject("p");
            go.transform.SetParent(root, false);
            mf[k] = go.AddComponent<MeshFilter>();
            var r = go.AddComponent<MeshRenderer>();
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            r.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            r.enabled = false;
            mr[k] = r;
            tr[k] = go.transform;
            return k;
        }
        // full: recycle the next non-persistent particle
        for (int i = 0; i < MaxParticles; i++)
        {
            int k = stealCursor;
            stealCursor = (stealCursor + 1) % MaxParticles;
            if (ps[k].alive && ps[k].kind == Kind.Fly) continue;
            if (ps[k].alive) Kill(k, false);
            return k;
        }
        return -1;
    }

    void Activate(int k, Shape shape, bool additive)
    {
        ref var p = ref ps[k];
        if (!p.alive) liveCount++;
        p.alive = true;
        p.additive = additive && additiveMat != null;
        if (p.kind != Kind.Twinkle && p.kind != Kind.StinkLine && p.kind != Kind.Fly) p.emitter = null;
        mf[k].sharedMesh = shape == Shape.Sphere ? sphereMesh : cubeMesh;
        mr[k].sharedMaterial = p.additive ? additiveMat : opaqueMat;
        SetColor(k, p.color);
        tr[k].SetPositionAndRotation(p.pos, p.rot);
        tr[k].localScale = Vector3.zero;
        mr[k].enabled = true;
    }

    void Kill(int k, bool toFreeList = true)
    {
        if (!ps[k].alive) return;
        ps[k].alive = false;
        ps[k].emitter = null;
        liveCount--;
        if (mr[k] != null) mr[k].enabled = false;
        if (toFreeList) free.Push(k);
    }

    void SetColor(int k, Color c)
    {
        mpb.SetColor(BaseColorId, c);
        mpb.SetColor(ColorId, c);
        mr[k].SetPropertyBlock(mpb);
    }

    bool EnsureResources()
    {
        if (opaqueMat != null && cubeMesh != null && sphereMesh != null) return true;
        var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        cube.SetActive(false); sphere.SetActive(false);
        cubeMesh = cube.GetComponent<MeshFilter>().sharedMesh;
        sphereMesh = sphere.GetComponent<MeshFilter>().sharedMesh;
        if (opaqueMat == null)
        {
            opaqueMat = RubbishAssets.Material("mat/fx_unlit");
            if (opaqueMat == null)
            {
                var baseMat = cube.GetComponent<MeshRenderer>().sharedMaterial;
                if (baseMat != null)
                {
                    opaqueMat = new Material(baseMat) { name = "FeelFx_Fallback", hideFlags = HideFlags.DontSave };
                    opaqueMat.enableInstancing = true;
                    ownedMaterials.Add(opaqueMat);
                }
            }
        }
        if (additiveMat == null) additiveMat = RubbishAssets.Material("mat/fx_additive");
        FeelRunner.SafeDestroy(cube);
        FeelRunner.SafeDestroy(sphere);
        return opaqueMat != null && cubeMesh != null && sphereMesh != null;
    }

    // ------------------------------------------------------------------ helpers

    void RefreshBounds(Emitter e)
    {
        e.nextBounds = clock + 1f;
        if (e.target == null) return;
        rendererBuffer.Clear();
        e.target.GetComponentsInChildren(false, rendererBuffer);
        bool any = false;
        var b = new Bounds(e.target.position, Vector3.zero);
        foreach (var r in rendererBuffer)
        {
            if (r == null || !r.enabled || r is ParticleSystemRenderer) continue;
            if (!any) { b = r.bounds; any = true; }
            else b.Encapsulate(r.bounds);
        }
        rendererBuffer.Clear();
        if (!any) b = new Bounds(e.target.position + Vector3.up * 0.25f, Vector3.one * 0.5f);
        e.center = b.center;
        e.centerOffset = b.center - e.target.position;
        e.radius = Mathf.Clamp(Mathf.Max(b.extents.x, b.extents.z), 0.1f, 4f);
        e.height = Mathf.Clamp(b.size.y, 0.1f, 6f);
    }

    static float GroundBelow(Vector3 pos)
    {
        int mask = ~((1 << 2) | (1 << Carryable.InteractableLayer));
        if (Physics.Raycast(pos + Vector3.up * 0.3f, Vector3.down, out var hit, 8f, mask, QueryTriggerInteraction.Ignore))
            return hit.point.y;
        return pos.y - 50f;
    }

    static Color Jitter(Color c)
    {
        float roll = Random.value;
        if (roll < 0.15f) return Color.Lerp(c, Color.white, 0.7f);
        Color.RGBToHSV(c, out float h, out float s, out float v);
        h = Mathf.Repeat(h + Random.Range(-0.04f, 0.04f), 1f);
        v = Mathf.Clamp01(v * Random.Range(0.8f, 1.15f));
        if (roll > 0.88f) v *= 0.7f;
        var o = Color.HSVToRGB(h, s, v);
        o.a = c.a;
        return o;
    }

    static float PopShrink(float age01, float popIn, float shrinkOut)
    {
        if (age01 < popIn) { float q = age01 / popIn; return q < 0.7f ? q / 0.7f * 1.15f : Mathf.Lerp(1.15f, 1f, (q - 0.7f) / 0.3f); }
        if (age01 > 1f - shrinkOut) return 1f - EaseIn((age01 - (1f - shrinkOut)) / shrinkOut);
        return 1f;
    }

    static float EaseIn(float p) { p = Mathf.Clamp01(p); return p * p; }
}
