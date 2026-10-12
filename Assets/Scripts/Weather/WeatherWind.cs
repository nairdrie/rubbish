using System.Diagnostics;
using UnityEngine;

/// <summary>
/// F5 weather, physics side (Docs/Multiplayer.md §14.5): gusts push loose trash and light bags that THIS machine simulates
/// (solo: everything; co-op: the host's resting items and whatever this machine is flying, never a copy that follows the
/// network, so no outcome depends on two machines' physics, §9 rule 13). Heavy bags (F3's weight class, or ≥ 15 kg) and
/// untouched bags still sitting at their curb never move. Outdoors only (the town is loaded, not inside a shop pocket).
/// Runs at 10 Hz on a budget: only items within <see cref="Range"/> of a player.
/// </summary>
[DefaultExecutionOrder(50)]
public sealed class WeatherWind : MonoBehaviour
{
    public const float Range = 70f, MinWind = 3.2f, StepSeconds = 0.1f;
    /// <summary>Drag constants (N per (m/s)²): bags are boxier, loose bits flutter.</summary>
    public const float BagDrag = 0.11f, LooseDrag = 0.07f, MaxAccel = 22f;

    float acc;
    uint rng = 0x9E3779B9u;
    static readonly Stopwatch watch = new Stopwatch();

    /// <summary>Items pushed in the last step (debug, bots).</summary>
    public static int LastPushed { get; private set; }
    /// <summary>Total pushes since boot.</summary>
    public static int TotalPushes { get; private set; }
    /// <summary>Accumulated cost (ms) since the last <see cref="TakeCostMs"/>.</summary>
    static double costMs;

    public static double TakeCostMs()
    {
        double c = costMs;
        costMs = 0;
        return c;
    }

    float nextPuddle;

    void Update()
    {
        // F3 hook: rain makes the ground slippery everywhere (players slide, trucks drift; Puddles.SlipAt) and leaves rain puddles.
        bool town = WorldLifeRunner.IsAwake;
        Puddles.RainWetness = town ? Weather.Wetness * (Weather.Kind == WeatherKind.Storm ? 1f : 0.65f) : 0f;
        if (!town || GameSession.IsConnectedGuest || Weather.Rain < 0.3f || Time.time < nextPuddle) return;
        nextPuddle = Time.time + (Weather.Kind == WeatherKind.Storm ? 3f : 6f);
        int rainPuddles = 0;
        var all = Puddles.All;
        for (int i = 0; i < all.Count; i++) if (all[i].kind == PuddleKind.Rain) rainPuddles++;
        if (rainPuddles >= MaxRainPuddles || Puddles.Count >= Puddles.Max - 16) return;
        if (!PickPlayer(out Vector3 at)) return;
        float a = Rand() * Mathf.PI * 2f, r = 4f + Rand() * 18f;
        Vector3 p = at + new Vector3(Mathf.Cos(a) * r, 0f, Mathf.Sin(a) * r);
        if (DepotRowLayout.InteriorAt(p) >= 0 || !Puddles.Ground(p, out var g)) return;
        Puddles.Spawn(g, 0.9f + Rand() * 0.9f, PuddleKind.Rain, 70f + Rand() * 50f);
    }

    public const int MaxRainPuddles = 14;

    bool PickPlayer(out Vector3 at)
    {
        at = default;
        var bodies = PlayerBody.All;
        if (bodies.Count > 0)
        {
            int start = (int)(Rand() * bodies.Count) % bodies.Count;
            for (int i = 0; i < bodies.Count; i++)
            {
                var b = bodies[(start + i) % bodies.Count];
                if (b == null || !Weather.IsOutdoors(b.transform.position)) continue;
                at = b.transform.position;
                return true;
            }
            return false;
        }
        var lp = PlayerController.Local; // coop:allow(local) solo: the only player is the local one
        if (lp == null) return false;
        at = lp.transform.position;
        return Weather.IsOutdoors(at);
    }

    void FixedUpdate()
    {
        acc += Time.fixedDeltaTime;
        if (acc < StepSeconds) return;
        float dt = acc;
        acc = 0f;
        watch.Restart();
        try { Step(dt); }
        finally
        {
            watch.Stop();
            costMs += watch.Elapsed.TotalMilliseconds;
        }
    }

    void Step(float dt)
    {
        LastPushed = 0;
        if (!WorldLifeRunner.IsAwake) return;
        Vector3 wind = Weather.Wind;
        float speed = wind.magnitude;
        if (speed < MinWind) return;
        Vector3 dir = wind / speed;

        var all = Carryable.All;
        for (int i = 0; i < all.Count; i++)
        {
            var c = all[i];
            if (c == null || c.NetRemote || c.Holder != null || c.IsInScriptedFlight) continue;
            if (c.State == CarryState.Carried || c.State == CarryState.Consumed) continue;
            var rb = c.Body;
            if (rb == null || rb.isKinematic) continue;
            // Keep untouched bags at their curb. Once a player has handled one, a later drop is fair game for the wind.
            if (c is TrashBag tb && (tb.HasBurst || (tb.SourceStop != null && tb.LastReleaseTime < 0f))) continue;
            if (YardDogs.IsInMouth(c)) continue;
            Vector3 p = c.transform.position;
            if (p.y < -5f || DepotRowLayout.InteriorAt(p) >= 0) continue;
            if (!NearPlayer(p)) continue;
            if (!Weather.IsLight(c)) continue;

            // Quadratic drag on the wind relative to the body; a little lift so it tumbles instead of sticking.
            Vector3 rel = wind - rb.linearVelocity;
            rel.y = 0f;
            float rs = rel.magnitude;
            if (rs < 0.5f) continue;
            float k = c is TrashBag ? BagDrag : LooseDrag;
            float force = k * rs * rs;
            float m = Mathf.Max(0.05f, rb.mass);
            float a = Mathf.Min(force / m, MaxAccel);
            Vector3 dv = rel / rs * a * dt;
            dv.y += a * dt * (c is TrashBag ? 0.25f : 0.45f);
            rb.AddForce(dv, ForceMode.VelocityChange);
            rb.AddTorque(Vector3.Cross(Vector3.up, dir) * (a * 0.15f) + new Vector3(Rand() - 0.5f, Rand() - 0.5f, Rand() - 0.5f) * 0.4f,
                ForceMode.VelocityChange);
            LastPushed++;
            TotalPushes++;
        }
    }

    float Rand()
    {
        rng ^= rng << 13;
        rng ^= rng >> 17;
        rng ^= rng << 5;
        return (rng & 0xFFFF) / 65536f;
    }

    static bool NearPlayer(Vector3 p)
    {
        const float r2 = Range * Range;
        var bodies = PlayerBody.All;
        if (bodies.Count > 0)
        {
            for (int i = 0; i < bodies.Count; i++)
            {
                var b = bodies[i];
                if (b != null && (b.transform.position - p).sqrMagnitude < r2) return true;
            }
            return false;
        }
        var lp = PlayerController.Local; // coop:allow(local) solo has no PlayerBody: the one local player is the only viewer
        return lp != null && (lp.transform.position - p).sqrMagnitude < r2;
    }
}
