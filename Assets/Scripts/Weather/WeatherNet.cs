using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>The host's weather word: kind, when it changed (server time), the gust seed and a serial.</summary>
public struct WeatherNetState : INetworkSerializable, IEquatable<WeatherNetState>
{
    public byte kind;
    public double since;
    public uint seed;
    public int serial;

    public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
    {
        s.SerializeValue(ref kind);
        s.SerializeValue(ref since);
        s.SerializeValue(ref seed);
        s.SerializeValue(ref serial);
    }

    public bool Equals(WeatherNetState o) => kind == o.kind && since.Equals(o.since) && seed == o.seed && serial == o.serial;
    public override bool Equals(object obj) => obj is WeatherNetState o && Equals(o);
    public override int GetHashCode() => serial;
}

/// <summary>
/// Package F5 weather, on <c>NetSession.prefab</c> (Docs/Multiplayer.md §14.5). The host (and solo, which hosts too) picks
/// the weather a few times per in-game day (temporarily held Clear in normal play) and writes one <see cref="WeatherNetState"/>;
/// every machine applies it to <see cref="Weather"/>, which derives wetness, gusts and slip from it and the server
/// clock, so nothing streams per frame. Late joiners get the variable with the spawn. NetAudit domain <c>weather</c>
/// (versionless): every machine must agree on (kind, serial, seed).
/// <para>Bots / tests: <c>-f5weather SEC</c> cycles Clear → Windy → Rain → Storm every SEC seconds;
/// <see cref="DebugSet"/> forces a state (host/solo).</para>
/// </summary>
[NetSessionComponent(Order = 40)]
public class WeatherNet : NetworkBehaviour
{
    public static WeatherNet Instance { get; private set; }

    // Pause changing weather in ordinary sessions for now. Keep the weather implementation available to
    // explicit developer probes and DebugSet, so Rain/Storm/Windy can be retuned without deleting it.
    const bool DynamicWeatherInNormalPlay = false;

    /// <summary>Seconds a state lasts (min, max); storms are shorter.</summary>
    public const float MinStateSeconds = 150f, MaxStateSeconds = 330f, MinStormSeconds = 80f, MaxStormSeconds = 170f;
    /// <summary>The first change of a session waits at least this long.</summary>
    public const float FirstChangeSeconds = 90f;

    readonly NetworkVariable<WeatherNetState> state = new NetworkVariable<WeatherNetState>();

    double nextChange;
    uint rng;
    float botCycle;
    int lastAppliedSerial = -1;

    public override void OnNetworkSpawn()
    {
        Instance = this;
        state.OnValueChanged += OnState;
        if (IsServer)
        {
            rng = (uint)(DateTime.UtcNow.Ticks ^ (DateTime.UtcNow.Ticks >> 29)) | 1u;
            botCycle = ArgFloat("-f5weather", 0f);
            double now = ClockNow();
            var s = new WeatherNetState { kind = (byte)WeatherKind.Clear, since = now - 60.0, seed = Next(), serial = 1 };
            state.Value = s;
            nextChange = botCycle > 0f ? now + botCycle
                       : DynamicWeatherInNormalPlay ? now + FirstChangeSeconds + Next01() * 120f
                       : double.PositiveInfinity;
        }
        ApplyState(state.Value);
        WeatherBoot.Ensure();
    }

    public override void OnNetworkDespawn()
    {
        state.OnValueChanged -= OnState;
        if (Instance == this) Instance = null;
        Weather.ResetLocal();
        lastAppliedSerial = -1;
    }

    public override void OnDestroy()
    {
        if (Instance == this) Instance = null;
        base.OnDestroy();
    }

    void OnState(WeatherNetState a, WeatherNetState b) => ApplyState(b);

    void ApplyState(WeatherNetState s)
    {
        if (s.serial == 0) return;
        Weather.Apply((WeatherKind)s.kind, s.since, s.seed, s.serial);
        if (s.serial != lastAppliedSerial)
        {
            lastAppliedSerial = s.serial;
            NetAudit.Event("weather", new Dictionary<string, object>
            {
                { "op", "apply" }, { "state", ((WeatherKind)s.kind).ToString() }, { "serial", s.serial }, { "seed", (long)s.seed }, { "server", IsServer },
            });
        }
    }

    /// <summary>The shared clock: the server's time (guests: their estimate of it). Offline: realtime.</summary>
    public static double ClockNow()
    {
        var w = Instance;
        if (w != null && w.IsSpawned && w.NetworkManager != null) return w.NetworkManager.ServerTime.Time;
        return Time.realtimeSinceStartupAsDouble;
    }

    void Update()
    {
        if (!IsSpawned || !IsServer) return;
        double now = ClockNow();
        if (botCycle <= 0f && !DynamicWeatherInNormalPlay)
        {
            // A developer-forced weather state still expires back to Clear after its requested hold.
            if (now >= nextChange)
            {
                if ((WeatherKind)state.Value.kind != WeatherKind.Clear) HostSet(WeatherKind.Clear);
                nextChange = double.PositiveInfinity;
            }
            return;
        }
        if (now < nextChange) return;
        var cur = (WeatherKind)state.Value.kind;
        WeatherKind next;
        if (botCycle > 0f) next = (WeatherKind)(((int)cur + 1) % 4);
        else next = PickNext(cur);
        HostSet(next);
        float dur = botCycle > 0f ? botCycle
                  : next == WeatherKind.Storm ? Mathf.Lerp(MinStormSeconds, MaxStormSeconds, Next01())
                  : Mathf.Lerp(MinStateSeconds, MaxStateSeconds, Next01());
        nextChange = now + dur;
    }

    WeatherKind PickNext(WeatherKind cur)
    {
        // Rows: from Clear / Windy / Rain / Storm; columns: to Clear, Windy, Rain, Storm (never the same twice).
        float[] w = cur switch
        {
            WeatherKind.Clear => new[] { 0f, 0.35f, 0.45f, 0.20f },
            WeatherKind.Windy => new[] { 0.40f, 0f, 0.40f, 0.20f },
            WeatherKind.Rain => new[] { 0.50f, 0.25f, 0f, 0.25f },
            _ => new[] { 0.20f, 0.20f, 0.60f, 0f },
        };
        float r = Next01(), acc = 0f;
        for (int i = 0; i < 4; i++)
        {
            acc += w[i];
            if (r < acc) return (WeatherKind)i;
        }
        return WeatherKind.Clear;
    }

    void HostSet(WeatherKind k)
    {
        var s = state.Value;
        s.kind = (byte)k;
        s.since = ClockNow();
        s.seed = Next();
        s.serial++;
        state.Value = s; // OnValueChanged fires on the host too
        NetAudit.Event("weather", new Dictionary<string, object> { { "op", "set" }, { "state", k.ToString() }, { "serial", s.serial } });
    }

    /// <summary>Host / solo: force a weather state now (tests, the debug menu). The next change follows the usual schedule.</summary>
    public static bool DebugSet(WeatherKind k, float holdSeconds = 240f)
    {
        var w = Instance;
        if (w == null || !w.IsSpawned || !w.IsServer) return false;
        w.HostSet(k);
        w.nextChange = ClockNow() + holdSeconds;
        return true;
    }

    // xorshift32 for the host's weather rolls (presentation-level world state; replicated, never re-rolled on guests)
    uint Next()
    {
        rng ^= rng << 13;
        rng ^= rng >> 17;
        rng ^= rng << 5;
        return rng;
    }

    float Next01() => (Next() & 0xFFFFFF) / 16777216f;

    internal static float ArgFloat(string name, float def)
    {
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) &&
                float.TryParse(args[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
                return v;
        return def;
    }

    // ------------------------------------------------------------------ audit

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void RegisterProbe() => NetAuditNet.RegisterProbe("weather", Probe);

    static bool Probe(NetworkManager nm, ulong forClient, out ulong hash, out int count)
    {
        hash = WorldHash.Seed;
        count = 0;
        var w = Instance;
        if (w == null || !w.IsSpawned) return false;
        var s = w.state.Value;
        hash = WorldHash.Fnv(hash, s.kind);
        hash = WorldHash.Fnv(hash, s.serial);
        hash = WorldHash.Fnv(hash, (long)s.seed);
        count = 1;
        return true;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => Instance = null;
}
