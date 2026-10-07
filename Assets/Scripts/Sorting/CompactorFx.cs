using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Local-only compactor presentation. Suction animates mesh snapshots, never the authoritative item,
/// its collider, or its transform. Call on every peer before consuming the selected stock. Lifetime and
/// mesh detail are bounded independently of pile size; interrupted effects restore surviving renderers.
/// </summary>
public sealed class CompactorFx : MonoBehaviour
{
    public const float SuctionDuration = 2.6f;
    public const int MaxVisibleGhosts = 80;
    public const int MaxMeshesPerGhost = 6;
    public const float BaleSettleSeconds = 0.7f;
    public const float BaleFloatSeconds = 1.4f;
    const int StreamCount = 12;
    const int StreamPoints = 10;

    sealed class Ghost
    {
        public Transform transform;
        public Vector3 from, scale;
        public Quaternion rotation;
        public int ordinal;
    }

    struct HiddenRenderer
    {
        public Renderer renderer;
        public bool enabled;
    }

    readonly List<Ghost> ghosts = new List<Ghost>();
    readonly List<HiddenRenderer> hidden = new List<HiddenRenderer>();
    readonly List<LineRenderer> streams = new List<LineRenderer>();
    readonly List<Vector3> streamStarts = new List<Vector3>();
    readonly Vector3[] streamPoints = new Vector3[StreamPoints];
    float elapsed, duration, nextWhoosh;
    int sourceCount;
    Vector3 mouth, baleFrom, baleScale;
    Quaternion baleRotation;
    bool initialized, baleMode, floating, cleaned;

    /// <summary>
    /// Copies visible meshes before callers consume stock. Only originals' renderer visibility is
    /// temporarily changed. Ghosts are collider-free; no rigidbodies, scripts, or networking are cloned.
    /// Optional short duration is useful for a normal chamber crush. Returns null outside play mode.
    /// </summary>
    public static CompactorFx BeginSuction(Transform owner, Vector3 mouth, IReadOnlyList<Carryable> sources,
        float duration = SuctionDuration)
    {
        if (!Application.isPlaying || owner == null) return null;
        var root = new GameObject("Compactor suction (cosmetic)");
        root.transform.SetParent(owner, false);
        var fx = root.AddComponent<CompactorFx>();
        fx.mouth = mouth;
        fx.duration = float.IsNaN(duration) || float.IsInfinity(duration)
            ? SuctionDuration : Mathf.Clamp(duration, 0.2f, SuctionDuration);
        fx.sourceCount = sources != null ? sources.Count : 0;
        fx.nextWhoosh = 0.12f;

        // Even sampling preserves the whole-room silhouette without thousands of extra renderers.
        int visible = Mathf.Min(MaxVisibleGhosts, fx.sourceCount);
        for (int i = 0; i < visible; i++)
        {
            int index = (int)((long)i * fx.sourceCount / visible);
            var source = sources[index];
            if (source == null) continue;
            fx.Snapshot(source, index);
        }
        var seenRenderers = new HashSet<Renderer>();
        if (sources != null)
            foreach (var source in sources)
            {
                if (source == null) continue;
                foreach (var r in source.GetComponentsInChildren<Renderer>(true))
                {
                    if (!seenRenderers.Add(r)) continue;
                    fx.hidden.Add(new HiddenRenderer { renderer = r, enabled = r.enabled });
                    r.enabled = false;
                }
            }

        fx.BuildStreams();
        fx.initialized = true;
        Sfx.Play("click", mouth, 0.6f, 0.65f);
        Sfx.Play("engine_start", mouth, 0.35f, 1.3f);
        return fx;
    }

    /// <summary>Explicitly retire an ordinary output bale. Never call on player avatars or carried stock.</summary>
    public static void FloatAwayBale(GameObject ordinaryBale)
    {
        if (!Application.isPlaying || !CanFloatBale(ordinaryBale)) return;
        if (ordinaryBale.GetComponent<CompactorFx>() != null) return;
        var fx = ordinaryBale.AddComponent<CompactorFx>();
        fx.baleMode = true;
        fx.initialized = true;
    }

    /// <summary>Player cube visuals are avatar children, not disposable machine output.</summary>
    public static bool CanFloatBale(GameObject candidate) => candidate != null
        && candidate.GetComponentInParent<PlayerController>() == null
        && candidate.GetComponentInParent<PlayerTells>() == null
        && candidate.GetComponentInParent<Carryable>() == null
        && candidate.GetComponentInParent<Unity.Netcode.NetworkObject>() == null;

    /// <summary>Spatial slam feedback. Camera shake is local and attenuated, not a map-wide jolt.</summary>
    public static void Slam(Transform visualRoot, Vector3 at, bool bulk = false)
    {
        Sfx.Play("compactor", at, 0.8f, bulk ? 0.8f : 1f);
        Sfx.Play("crunch", at, 0.7f, bulk ? 0.75f : 0.95f);
        Sfx.Play("slam", at, 0.65f);
        Fx.Dust(at, bulk ? 1.8f : 1.2f);
        if (visualRoot != null) Juice.Squash(visualRoot, 0.14f, 0.24f);
        var local = PlayerController.Local; // coop:allow(local) local-only camera shake attenuation; no gameplay state
        float distance = local != null ? Vector3.Distance(local.transform.position, at) : float.PositiveInfinity;
        float intensity = Mathf.Clamp01(1f - distance / 14f);
        if (intensity > 0f) Juice.Shake((bulk ? 0.38f : 0.26f) * intensity, 0.24f);
    }

    /// <summary>Fixed completion time even for huge piles; a short wind-up precedes a staggered intake.</summary>
    public static float TravelProgress(float seconds, int ordinal, int total, float duration = SuctionDuration)
    {
        if (float.IsNaN(seconds)) return 0f;
        if (float.IsNaN(duration) || float.IsInfinity(duration)) duration = SuctionDuration;
        float phase = seconds / Mathf.Max(0.01f, duration);
        float stagger = total <= 1 ? 0f : Mathf.Clamp01((float)ordinal / (total - 1));
        float start = 0.14f + 0.20f * stagger;
        return Mathf.Clamp01((phase - start) / (1f - start));
    }

    /// <summary>
    /// Deterministic endpoint-exact airborne path: visible lift, a corkscrew, then acceleration into
    /// the intake. Presentation only, shared by ghosts, wind ribbons, and owner-driven player suction.
    /// </summary>
    public static Vector3 SuctionPoint(Vector3 from, Vector3 to, float progress, int ordinal = 0)
    {
        if (float.IsNaN(progress) || progress <= 0f) return from;
        if (progress >= 1f) return to;
        float t = Mathf.Clamp01(progress);
        Vector3 travel = to - from;
        float distance = travel.magnitude;
        Vector3 forward = distance > 0.01f ? travel / distance : Vector3.forward;
        Vector3 side = Vector3.Cross(Vector3.up, forward);
        if (side.sqrMagnitude < 0.001f) side = Vector3.right;
        side.Normalize();
        Vector3 up = Vector3.Cross(forward, side).normalized;
        float arch = Mathf.Sin(Mathf.PI * t);
        float angle = t * Mathf.PI * 2.2f + ordinal * 2.399963f;
        float radius = Mathf.Min(0.65f, 0.08f + distance * 0.075f) * arch;
        Vector3 center = Vector3.LerpUnclamped(from, to, Mathf.Pow(t, 1.5f));
        return center + Vector3.up * (Mathf.Min(1.35f, 0.25f + distance * 0.12f) * arch)
            + (side * Mathf.Sin(angle) + up * Mathf.Cos(angle)) * radius;
    }

    /// <summary>Immediately stops this visual transaction and restores any source that still exists.</summary>
    public void Cancel()
    {
        Cleanup();
        if (gameObject != null) Destroy(gameObject);
    }

    void Snapshot(Carryable source, int ordinal)
    {
        var root = new GameObject("Vacuum trash ghost").transform;
        root.SetParent(transform, false);
        root.SetPositionAndRotation(source.transform.position, Quaternion.identity);
        int copied = 0;
        var block = new MaterialPropertyBlock();
        foreach (var original in source.GetComponentsInChildren<MeshRenderer>())
        {
            if (!original.enabled || !original.gameObject.activeInHierarchy || original.GetComponent<TMP_Text>() != null) continue;
            var filter = original.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null) continue;
            var go = new GameObject(original.name);
            go.transform.SetPositionAndRotation(original.transform.position, original.transform.rotation);
            go.transform.localScale = original.transform.lossyScale;
            go.transform.SetParent(root, true);
            go.AddComponent<MeshFilter>().sharedMesh = filter.sharedMesh;
            var copy = go.AddComponent<MeshRenderer>();
            copy.sharedMaterials = original.sharedMaterials;
            copy.shadowCastingMode = ShadowCastingMode.Off;
            copy.receiveShadows = false;
            original.GetPropertyBlock(block);
            copy.SetPropertyBlock(block);
            if (++copied >= MaxMeshesPerGhost) break;
        }
        if (copied == 0)
        {
            var visual = SortingKit.VisualCube("Trash silhouette", root, Vector3.zero, Vector3.one * 0.24f,
                SortingKit.RuntimeMaterial("mat/compactor_suction_scrap", new Color(0.44f, 0.47f, 0.38f)));
            var collider = visual.GetComponent<Collider>();
            if (collider != null) collider.enabled = false;
        }
        ghosts.Add(new Ghost { transform = root, from = root.position, scale = root.localScale,
            rotation = root.rotation, ordinal = ordinal });
    }

    void BuildStreams()
    {
        var mat = SortingKit.RuntimeMaterial("mat/compactor_suction_air", new Color(0.63f, 0.84f, 0.91f), true);
        for (int i = 0; i < StreamCount; i++)
        {
            var go = new GameObject("Intake wind ribbon");
            go.transform.SetParent(transform, false);
            var line = go.AddComponent<LineRenderer>();
            line.sharedMaterial = mat;
            line.useWorldSpace = true;
            line.positionCount = StreamPoints;
            line.widthMultiplier = 0.018f;
            line.widthCurve = AnimationCurve.EaseInOut(0f, 0.06f, 1f, 1f);
            line.shadowCastingMode = ShadowCastingMode.Off;
            line.receiveShadows = false;
            line.enabled = false;
            streams.Add(line);
            if (ghosts.Count > 0) streamStarts.Add(ghosts[i * ghosts.Count / StreamCount].from);
            else
            {
                float angle = i * Mathf.PI * 2f / StreamCount;
                streamStarts.Add(mouth + new Vector3(Mathf.Cos(angle) * 2.8f, -0.6f, Mathf.Sin(angle) * 2.8f));
            }
        }
    }

    void Update()
    {
        if (!initialized || cleaned) return;
        elapsed += Time.deltaTime;
        if (baleMode) { UpdateBale(); return; }
        if (elapsed >= duration) { Cancel(); return; }
        float phase = elapsed / duration;
        foreach (var ghost in ghosts)
        {
            if (ghost.transform == null) continue;
            float p = TravelProgress(elapsed, ghost.ordinal, sourceCount, duration);
            ghost.transform.position = SuctionPoint(ghost.from, mouth, p, ghost.ordinal);
            // A brief anticipatory rattle, then tumbling acceleration; shrink only at the mouth.
            if (p <= 0f)
                ghost.transform.rotation = Quaternion.AngleAxis(Mathf.Sin(phase * 90f + ghost.ordinal) * 3f, Vector3.forward) * ghost.rotation;
            else ghost.transform.rotation = Quaternion.Euler(p * 280f, p * (120f + ghost.ordinal % 5 * 35f), p * 210f) * ghost.rotation;
            float scale = p < 0.78f ? 1f : Mathf.Lerp(1f, 0.035f, (p - 0.78f) / 0.22f);
            ghost.transform.localScale = ghost.scale * scale;
        }
        for (int i = 0; i < streams.Count; i++)
        {
            var line = streams[i];
            float head = Mathf.Repeat(phase * 2.3f + i * 0.137f, 1f);
            bool visible = phase >= 0.12f && phase < 0.95f && head > 0.15f;
            line.enabled = visible;
            if (!visible) continue;
            for (int p = 0; p < StreamPoints; p++)
                streamPoints[p] = SuctionPoint(streamStarts[i], mouth,
                    Mathf.Lerp(Mathf.Max(0f, head - 0.15f), head, (float)p / (StreamPoints - 1)), i);
            line.SetPositions(streamPoints);
        }
        if (phase >= nextWhoosh)
        {
            nextWhoosh = phase + 0.21f;
            Sfx.Play("whoosh", mouth, 0.35f + phase * 0.2f, 0.65f + phase * 0.55f);
        }
    }

    void UpdateBale()
    {
        if (elapsed < BaleSettleSeconds) return;
        if (!floating)
        {
            floating = true;
            foreach (var c in GetComponentsInChildren<Collider>()) c.enabled = false;
            foreach (var rb in GetComponentsInChildren<Rigidbody>())
            {
                if (!rb.isKinematic) { rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
                rb.isKinematic = true;
            }
            baleFrom = transform.position;
            baleScale = transform.localScale;
            baleRotation = transform.rotation;
            Fx.Sparkle(transform, new Color(0.73f, 0.94f, 0.72f), BaleFloatSeconds);
        }
        float t = Mathf.Clamp01((elapsed - BaleSettleSeconds) / BaleFloatSeconds);
        transform.position = baleFrom + Vector3.up * (1.8f * t * (0.65f + 0.35f * t));
        transform.rotation = Quaternion.AngleAxis(80f * t, Vector3.up) * baleRotation;
        float shrink = t < 0.7f ? 1f : 1f - Mathf.SmoothStep(0f, 1f, (t - 0.7f) / 0.3f);
        transform.localScale = baleScale * shrink;
        if (t >= 1f) Destroy(gameObject);
    }

    void OnDisable()
    {
        Cleanup();
        // The machine can be deactivated when the last visitor leaves its interior. Do not leave an
        // inert child behind on each interrupted vacuum; this root owns no persistent content.
        if (initialized && !baleMode && Application.isPlaying) Destroy(gameObject);
    }
    void OnDestroy() => Cleanup();

    void Cleanup()
    {
        if (cleaned) return;
        cleaned = true;
        foreach (var h in hidden) if (h.renderer != null) h.renderer.enabled = h.enabled;
        hidden.Clear();
        foreach (var ghost in ghosts) if (ghost.transform != null) Destroy(ghost.transform.gameObject);
        ghosts.Clear();
        foreach (var line in streams) if (line != null) Destroy(line.gameObject);
        streams.Clear();
        streamStarts.Clear();
    }
}
