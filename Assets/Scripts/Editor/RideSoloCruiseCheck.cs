using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>Focused executable check for F1 solo cruise. Run in the isolated build copy with
/// Unity -batchmode -nographics -quit -projectPath PATH -executeMethod RideSoloCruiseCheck.Run.</summary>
public static class RideSoloCruiseCheck
{
    public static void Run()
    {
        var graph = StreetGraph.Empty();
        var go = new GameObject("Solo cruise check truck");
        try
        {
            // One directed lane with a continuation, far from scene colliders so the sensor has a clear road.
            const float x = 10000f, z = 10000f;
            graph.nodes = new[]
            {
                new StreetGraph.Node { pos = new Vector3(x, 0f, z), kind = StreetGraph.NodeKind.Lane, gate = StreetGraph.None },
                new StreetGraph.Node { pos = new Vector3(x, 0f, z + 20f), kind = StreetGraph.NodeKind.Lane, gate = StreetGraph.None },
                new StreetGraph.Node { pos = new Vector3(x, 0f, z + 40f), kind = StreetGraph.NodeKind.Lane, gate = StreetGraph.None },
            };
            graph.edges = new[]
            {
                new StreetGraph.Edge { a = 0, b = 1, kind = StreetGraph.EdgeKind.Road, len = 20f },
                new StreetGraph.Edge { a = 1, b = 2, kind = StreetGraph.EdgeKind.Road, len = 20f },
            };
            graph.Invalidate();
            go.AddComponent<NetworkObject>();
            var body = go.AddComponent<Rigidbody>();
            var truck = go.AddComponent<TruckControllerV2>();
            go.transform.SetPositionAndRotation(new Vector3(x, 0f, z + 5f), Quaternion.identity);

            Check(SoloCruiseRoute.TryBuild(graph, truck, 1, out var route), "aligned parked truck gets a route");
            Check(route.Length > 30f, "route includes the directed continuation");
            route.Tick(truck, body, 0.02f, null);
            Check(!route.Done && route.Command.y > 0f && Mathf.Abs(route.Command.x) < 0.5f,
                "clear route asks for gentle forward motion");

            go.transform.rotation = Quaternion.Euler(0f, 180f, 0f);
            Check(!SoloCruiseRoute.TryBuild(graph, truck, 1, out _), "wrong-way truck is refused");
            go.transform.position = new Vector3(x + 30f, 0f, z + 5f);
            go.transform.rotation = Quaternion.identity;
            Check(!SoloCruiseRoute.TryBuild(graph, truck, 1, out _), "off-road truck is refused");

            go.transform.position = new Vector3(x, 0f, z + 5f);
            var closed = graph.edges[0];
            closed.flags = StreetGraph.EdgeFlags.GateLocked;
            graph.edges[0] = closed;
            graph.Invalidate();
            Check(!SoloCruiseRoute.TryBuild(graph, truck, 0, out _), "locked lane is refused");

            // A reliable old jolt can arrive after a newer future-stamped one. It must fire at the visible tick,
            // without waiting for the future event or firing twice on subsequent frames.
            var ride = go.AddComponent<RideTruck>();
            int subscribeAt = ride.LastJoltSerial;
            var seen = new HashSet<int>();
            var ready = new List<RideTruck.Jolt>();
            ride.ReceiveJolt(new RideTruck.Jolt { t = 20.0, cost = 25f });
            ride.ReceiveJolt(new RideTruck.Jolt { t = 10.0, cost = 40f });
            ride.JoltsSince(subscribeAt, 10.0, seen, ready);
            Check(ready.Count == 1 && ready[0].cost == 40f, "late old jolt crosses a future receipt");
            ready.Clear();
            ride.JoltsSince(subscribeAt, 10.0, seen, ready);
            Check(ready.Count == 0, "old jolt is not duplicated");
            ride.JoltsSince(subscribeAt, 20.0, seen, ready);
            Check(ready.Count == 1 && ready[0].cost == 25f, "future jolt waits for its visible tick");
            for (int j = 0; j < 40; j++) ride.ReceiveJolt(new RideTruck.Jolt { t = 30.0 + j, cost = j });
            ready.Clear();
            ride.JoltsSince(subscribeAt, 100.0, seen, ready);
            Check(seen.Count <= 24, "consumed receipt history stays bounded to retained jolts");
            Debug.Log("[RideSoloCruiseCheck] PASS: route, forward command, wrong-way, off-road, locked-lane and jolt receipt checks");
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(go);
            UnityEngine.Object.DestroyImmediate(graph);
        }
    }

    static void Check(bool ok, string what)
    {
        if (!ok) throw new Exception("[RideSoloCruiseCheck] FAIL: " + what);
    }
}
