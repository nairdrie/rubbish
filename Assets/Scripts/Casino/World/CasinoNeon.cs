using UnityEngine;

/// <summary>
/// A glowing sign: neon tubes that breathe and now and then buzz out, or marquee bulbs that chase. Purely cosmetic and
/// local (each machine flickers on its own; nothing is networked). Drives the emission of its renderers through a
/// MaterialPropertyBlock, plus an optional light.
/// <para><b>Accessibility:</b> nothing here strobes. A chasing bulb changes 1.25 times a second; a buzz-out dips about
/// twice a second and never switches instantly (the level is slewed). Both depths scale with
/// <see cref="GameSettings.ScreenFx01"/> and both stop entirely with <see cref="GameSettings.ReduceFlashing"/>.</para>
/// </summary>
public class CasinoNeon : MonoBehaviour
{
    public enum Mode { Neon, Chase, Steady }

    public Mode mode = Mode.Neon;
    [Tooltip("Neon / Steady: all lit together. Chase: lit in turn (A, B, A, B ...).")]
    public Renderer[] renderers = new Renderer[0];
    [ColorUsage(false, true)] public Color emission = new Color(4f, 0.5f, 0.4f);
    public Light glow;
    public float glowIntensity = 1f;
    [Tooltip("How often a tube buzzes out (0 = never).")]
    [Range(0f, 1f)] public float flicker = 0.35f;
    [Tooltip("Chase steps per second (keep at or under 2.5: each bulb then changes 1.25 times a second).")]
    public float chaseRate = 2.5f;
    [Tooltip("Scale with the time of day: dayLevel in daylight, full at night (street signs).")]
    public bool nightOnly;
    [Range(0f, 1f)] public float dayLevel = 0.45f;
    public int seed = 1;

    public const float MaxFlashHz = 2.5f;

    static readonly int EmissionId = Shader.PropertyToID("_EmissionColor");
    MaterialPropertyBlock block;
    float buzzUntil, nextBuzz, level = 1f;
    uint rng;

    void OnEnable()
    {
        rng = (uint)(seed * 747796405) + 2891336453u;
        nextBuzz = Time.time + 3f + Next() * 8f;
        level = 1f;
        Apply(1f, 0f, 1f);
    }

    // A tiny private LCG: cosmetic only, never touches gameplay dice.
    float Next()
    {
        rng = rng * 1664525u + 1013904223u;
        return (rng >> 8) / 16777216f;
    }

    void Update()
    {
        float t = Time.time;
        float fx = Application.isPlaying ? (GameSettings.ReduceFlashing ? 0f : Mathf.Clamp01(GameSettings.ScreenFx01)) : 1f;
        float target = 1f;
        if (mode == Mode.Neon)
        {
            target = 0.96f + 0.04f * Mathf.Sin(t * 4.1f + seed); // the tube breathing
            if (flicker > 0f && fx > 0.01f)
            {
                if (t >= nextBuzz)
                {
                    buzzUntil = t + 0.6f + Next() * 1.1f;
                    nextBuzz = buzzUntil + Mathf.Lerp(26f, 5f, flicker) * (0.6f + Next());
                }
                if (t < buzzUntil)
                {
                    float depth = Mathf.Lerp(0f, 0.7f, fx);
                    bool dip = Mathf.Sin(t * Mathf.PI * 2f * 2.1f + seed) > 0.25f;
                    if (dip) target *= 1f - depth;
                }
            }
        }
        level = Mathf.MoveTowards(level, target, Time.deltaTime * 7f); // never an instant switch
        float day = nightOnly ? Mathf.Lerp(dayLevel, 1f, GameClock.Night01) : 1f;
        Apply(level * day, t, fx);
    }

    void Apply(float k, float t, float fx)
    {
        if (block == null) block = new MaterialPropertyBlock();
        float rate = Mathf.Min(chaseRate, MaxFlashHz);
        int step = mode == Mode.Chase ? Mathf.FloorToInt(t * rate) : 0;
        float off = Mathf.Lerp(1f, 0.2f, fx); // reduced effects: the bulbs just stay lit
        for (int i = 0; i < renderers.Length; i++)
        {
            var r = renderers[i];
            if (r == null) continue;
            float on = mode == Mode.Chase ? (((i + step) & 1) == 0 ? 1f : off) : 1f;
            r.GetPropertyBlock(block);
            block.SetColor(EmissionId, emission * (k * on));
            r.SetPropertyBlock(block);
        }
        if (glow != null) glow.intensity = glowIntensity * k * (nightOnly ? Mathf.Lerp(0.15f, 1f, GameClock.Night01) : 1f);
    }
}
