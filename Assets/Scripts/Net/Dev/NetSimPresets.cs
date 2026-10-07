using System;
using System.Collections.Generic;
using System.Globalization;
using Unity.Collections;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Networking.Transport;
using Unity.Networking.Transport.Utilities;
using UnityEngine;

/// <summary>
/// Bad-network presets for bots and play tests (Docs/Multiplayer.md §8.7.4, package B5; §4 "Crew Bad Wi-Fi").
/// <para>
/// NGO 2.x turned <c>UnityTransport.SetDebugSimulatorParameters</c> into a no-op (it now defers to the Multiplayer Tools
/// package, which this project doesn't use), so <see cref="Apply"/> installs a <see cref="UnityTransport.s_DriverConstructor"/>
/// that builds the default driver and appends UTP's <c>SimulatorPipelineStage</c> to all three pipelines. Every machine applies
/// its preset to the packets it SENDS, so a preset's delay is one way: <c>crew_bad_wifi</c> = 75 ms ± 10 ms and 2% loss each
/// way ≈ 150 ms round trip, ±20 ms, the "SetDebugSimulatorParameters(75, 10, 2)" of the plan.
/// </para>
/// Call before <c>StartHost</c>/<c>StartClient</c> (the driver is created there). Development builds and the Editor only.
/// <c>-netsim &lt;preset&gt;</c> on a bot's command line, or <c>-netsim 100,15,5</c> (delay ms, jitter ms, loss %).
/// </summary>
public static class NetSimPresets
{
    public const string Flag = "-netsim";

    public readonly struct Preset
    {
        public readonly string Name;
        /// <summary>One-way delay, jitter (±) and loss applied to every packet this machine sends.</summary>
        public readonly int DelayMs, JitterMs, LossPercent;

        public Preset(string name, int delayMs, int jitterMs, int lossPercent)
        {
            Name = name;
            DelayMs = Mathf.Clamp(delayMs, 0, 2000);
            JitterMs = Mathf.Clamp(jitterMs, 0, DelayMs);
            LossPercent = Mathf.Clamp(lossPercent, 0, 90);
        }

        public bool IsOff => DelayMs == 0 && JitterMs == 0 && LossPercent == 0;
        public int RoundTripMs => DelayMs * 2;
        public override string ToString() =>
            IsOff ? $"{Name} (off)" : $"{Name}: {DelayMs}±{JitterMs} ms each way (≈{RoundTripMs} ms round trip), {LossPercent}% loss";
    }

    static readonly Dictionary<string, Preset> table = new Dictionary<string, Preset>(StringComparer.OrdinalIgnoreCase)
    {
        { "none", new Preset("none", 0, 0, 0) },
        { "home_wifi", new Preset("home_wifi", 25, 5, 0) },
        { "crew_bad_wifi", new Preset("crew_bad_wifi", 75, 10, 2) },      // the M0/M1 gates
        { "far_friend", new Preset("far_friend", 120, 20, 3) },
        { "awful", new Preset("awful", 175, 40, 8) },
    };

    public static IEnumerable<string> Names => table.Keys;

    /// <summary>The preset in effect for the next StartHost/StartClient (null = none).</summary>
    public static Preset? Active { get; private set; }

    /// <summary>A preset name or "delay,jitter,loss". False (and a reason) for anything else.</summary>
    public static bool TryParse(string spec, out Preset preset, out string error)
    {
        preset = default;
        error = null;
        if (string.IsNullOrWhiteSpace(spec)) { error = "empty preset"; return false; }
        spec = spec.Trim();
        if (table.TryGetValue(spec, out preset)) return true;
        string[] parts = spec.Split(',');
        if (parts.Length == 3 &&
            int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int d) &&
            int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int j) &&
            int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int l) && d >= 0 && j >= 0 && l >= 0)
        {
            preset = new Preset($"custom {d},{j},{l}", d, j, l);
            return true;
        }
        error = $"unknown {Flag} preset '{spec}' (use {string.Join(", ", table.Keys)}, or delay,jitter,loss)";
        return false;
    }

    /// <summary>
    /// Use <paramref name="spec"/> for the next transport start. "none" clears it. Returns false (logged) for a bad spec, a release
    /// build, or a transport that is already running.
    /// </summary>
    public static bool Apply(NetworkManager nm, string spec, uint seed)
    {
        if (!TryParse(spec, out Preset p, out string error))
        {
            Debug.LogWarning("[NetSim] " + error);
            return false;
        }
        return Apply(nm, p, seed);
    }

    public static bool Apply(NetworkManager nm, Preset p, uint seed)
    {
        if (p.IsOff)
        {
            Clear();
            return true;
        }
        if (!Debug.isDebugBuild && !Application.isEditor)
        {
            Debug.LogWarning($"[NetSim] {p.Name} ignored: network simulation is for development builds only.");
            return false;
        }
        var transport = nm != null ? nm.NetworkConfig?.NetworkTransport as UnityTransport : null;
        if (transport == null)
        {
            Debug.LogWarning("[NetSim] No UnityTransport to simulate on.");
            return false;
        }
        if (transport.UseWebSockets)
        {
            Debug.LogWarning($"[NetSim] {p.Name} ignored: WebSocket transports aren't simulated.");
            return false;
        }
        if (nm.IsListening)
        {
            Debug.LogWarning($"[NetSim] {p.Name} ignored: the transport is already running (apply it before StartHost/StartClient).");
            return false;
        }
        UnityTransport.s_DriverConstructor = new SimDriverConstructor(p, seed == 0 ? 1u : seed);
        Active = p;
        Debug.Log("[NetSim] " + p);
        NetAudit.Event("netsim", new Dictionary<string, object>
        {
            { "preset", p.Name }, { "delayMs", p.DelayMs }, { "jitterMs", p.JitterMs }, { "lossPercent", p.LossPercent },
        });
        return true;
    }

    /// <summary>Back to the transport's own driver (no simulation) for the next start.</summary>
    public static void Clear()
    {
        if (UnityTransport.s_DriverConstructor is SimDriverConstructor) UnityTransport.s_DriverConstructor = null;
        Active = null;
    }

    /// <summary>The default UTP driver and pipelines, plus SimulatorPipelineStage (last) on each pipeline.</summary>
    sealed class SimDriverConstructor : INetworkStreamDriverConstructor
    {
        readonly Preset preset;
        readonly uint seed;

        public SimDriverConstructor(Preset p, uint seed)
        {
            preset = p;
            this.seed = seed;
        }

        public void CreateDriver(UnityTransport transport, out NetworkDriver driver, out NetworkPipeline unreliableFragmentedPipeline,
            out NetworkPipeline unreliableSequencedFragmentedPipeline, out NetworkPipeline reliableSequencedPipeline)
        {
            var settings = transport.GetDefaultNetworkSettings();
            // 1024 queued packets per connection covers ~200 ms of a 30 Hz tick's reliable window in both directions.
            settings.WithSimulatorStageParameters(maxPacketCount: 1024, mode: ApplyMode.SentPacketsOnly,
                packetDelayMs: preset.DelayMs, packetJitterMs: preset.JitterMs, packetDropPercentage: preset.LossPercent,
                randomSeed: seed);
            driver = NetworkDriver.Create(new UDPNetworkInterface(), settings);

            transport.GetDefaultPipelineConfigurations(ref driver, out var uf, out var usf, out var rs);
            unreliableFragmentedPipeline = driver.CreatePipeline(WithSimulator(uf));
            unreliableSequencedFragmentedPipeline = driver.CreatePipeline(WithSimulator(usf));
            reliableSequencedPipeline = driver.CreatePipeline(WithSimulator(rs));
        }

        static NativeArray<NetworkPipelineStageId> WithSimulator(NativeArray<NetworkPipelineStageId> stages)
        {
            var sim = NetworkPipelineStageId.Get<SimulatorPipelineStage>();
            var list = new NativeArray<NetworkPipelineStageId>(stages.Length + 1, Allocator.Temp);
            for (int i = 0; i < stages.Length; i++)
            {
                if (stages[i].Equals(sim)) return stages; // already simulated (Multiplayer Tools installed): keep its setup
                list[i] = stages[i];
            }
            list[stages.Length] = sim;
            return list;
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => Active = null;
}
