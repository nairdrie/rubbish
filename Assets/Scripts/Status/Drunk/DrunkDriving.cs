using System;
using UnityEngine;

/// <summary>
/// Drunk driving (Docs/Casino.md §5): while this machine's player is in the driver's seat, the truck's steering wanders,
/// lags behind the wheel and over-corrects, and the throttle is unsteady. The same for the Hoverhog's pilot.
///
/// It goes through the vehicles' existing input overrides, on the machine that reads the input (in co-op the driver owns
/// the truck's physics, and the pilot flies the Hoverhog locally), and only ever as a filter over the player's own keys:
/// <list type="bullet">
/// <item><see cref="TruckControllerV2.InputOverride"/> is taken only while it is free AND the local player is the confirmed
/// driver (seated; in a session also the driver of record and the owner), so "is anyone driving" and "does this machine
/// drive" answer exactly as they did without it. Bots, the F1 rig and RideTruck's solo cruise set the override only on a
/// truck with no seated driver, and they find it free: it is dropped the moment the seat, the session role, a UI lock or
/// sobriety says so (RideTruck refuses a truck whose override is taken, so it must never linger).</item>
/// <item><see cref="Hoverhog.PilotInputOverride"/> is taken only while it is free and <see cref="Hoverhog.LocalSeat"/> is set.</item>
/// </list>
/// Lives on the <see cref="DrunkRunner"/> object.
/// </summary>
[DisallowMultipleComponent]
public sealed class DrunkDriving : MonoBehaviour
{
    TruckControllerV2 hookedTruck;
    Func<Vector2> truckFilter;
    Func<Vector3> hogFilter;
    bool hogHooked;

    // filter state (shared: you are in one seat at a time)
    float steer, steerVel, lastTime = -1f, seed;
    Vector2 lastOut;
    Vector3 lastHogOut;
    float hogLastTime = -1f;

    void Awake()
    {
        truckFilter = TruckInput;
        hogFilter = HogInput;
        seed = NightDice.Cosmetic.Range(0f, 100f);
    }

    void OnDisable() => Release();
    void OnDestroy() => Release();

    void Release()
    {
        UnhookTruck();
        UnhookHog();
    }

    static float Drive01 => DrunkStatus.PassingOut ? 0f : DrunkRules.Drive01(DrunkStatus.Level);

    static bool UiHoldsVehicle => (PlayerController.IsUIBlocking && !FullMap.DrivingAllowed) || SceneController.IsTransitioning;

    void Update()
    {
        float d = Drive01;
        TickTruck(d);
        TickHog(d);
    }

    // ------------------------------------------------------------------ truck

    /// <summary>This machine's player is this truck's confirmed driver, and this machine is the one simulating it.</summary>
    static bool LocalDrives(TruckControllerV2 truck) =>
        truck != null && truck.isActiveAndEnabled && truck.IsLocalPlayerSeated
        && (!truck.IsSpawned || (truck.IsLocalDriver && truck.IsOwner));

    void TickTruck(float d)
    {
        var me = NightPlayers.LocalPlayer;
        var truck = me != null && me.IsDriving ? me.currentTruck : null;
        // With a UI lock the truck must hold its brakes, which it only does when no override is set: step aside.
        bool want = d > 0.01f && LocalDrives(truck) && !UiHoldsVehicle;

        if (hookedTruck != null && (!want || hookedTruck != truck)) UnhookTruck();
        if (hookedTruck != null && hookedTruck.InputOverride != truckFilter) hookedTruck = null; // someone cleared or replaced it
        if (!want || hookedTruck == truck) return;
        if (truck.InputOverride != null) return; // somebody else's (a bot, a cruise): never fight it
        steer = 0f; steerVel = 0f; lastTime = -1f;
        truck.InputOverride = truckFilter;
        hookedTruck = truck;
    }

    void UnhookTruck()
    {
        if (hookedTruck != null && hookedTruck.InputOverride == truckFilter) hookedTruck.InputOverride = null;
        hookedTruck = null;
    }

    /// <summary>What the truck reads instead of the raw axes (x steer, y throttle). Called from its FixedUpdate, and by the
    /// grade assist in the same step: one answer per physics step.</summary>
    Vector2 TruckInput()
    {
        var truck = hookedTruck;
        float d = Drive01;
        // The seat emptied between our Update and this physics step: let go now rather than drive a ghost.
        if (!LocalDrives(truck) || UiHoldsVehicle)
        {
            UnhookTruck();
            return Vector2.zero;
        }
        float now = Time.time;
        if (now == lastTime) return lastOut;
        float dt = lastTime < 0f ? Time.fixedDeltaTime : Mathf.Clamp(now - lastTime, 0.001f, 0.1f);
        lastTime = now;

        Vector2 raw = GameControls.DriveAxes();
        lastOut = new Vector2(Steer(raw.x, d, dt, now), Throttle(raw.y, d, now));
        return lastOut;
    }

    /// <summary>The wheel follows your hands late and swings past where you meant (an under-damped spring), and wanders by itself.</summary>
    float Steer(float want, float d, float dt, float now)
    {
        float omega = Mathf.Lerp(26f, 5.5f, d);  // how quickly the wheel answers (rad/s): sober = at once
        float zeta = Mathf.Lerp(1f, 0.32f, d);   // damping: below 1 it overshoots and corrects back
        float acc = omega * omega * (want - steer) - 2f * zeta * omega * steerVel;
        steerVel += acc * dt;
        steer += steerVel * dt;
        steer = Mathf.Clamp(steer, -1.3f, 1.3f);
        float t = now + seed;
        float wander = (Mathf.Sin(t * 0.83f) * 0.6f + Mathf.Sin(t * 2.3f + 1.1f) * 0.4f) * Mathf.Lerp(0f, 0.34f, d);
        return Mathf.Clamp(steer + wander, -1f, 1f);
    }

    /// <summary>An unsteady foot: the pedal you hold flutters, but a lifted foot stays lifted (you can always stop).</summary>
    float Throttle(float want, float d, float now)
    {
        if (Mathf.Abs(want) < 0.01f) return want;
        float t = now + seed * 1.7f;
        float flutter = (Mathf.Sin(t * 1.9f) * 0.6f + Mathf.Sin(t * 4.7f + 0.4f) * 0.4f) * 0.22f * d;
        return Mathf.Clamp(want * (1f + flutter), -1f, 1f);
    }

    // ------------------------------------------------------------------ Hoverhog

    void TickHog(float d)
    {
        bool want = d > 0.01f && Hoverhog.LocalSeat != null && !UiHoldsVehicle;
        if (hogHooked && Hoverhog.PilotInputOverride != hogFilter) hogHooked = false; // someone cleared or replaced it
        if (hogHooked && !want) UnhookHog();
        if (!want || hogHooked) return;
        if (Hoverhog.PilotInputOverride != null) return; // a bot or a probe flies it
        steer = 0f; steerVel = 0f; hogLastTime = -1f;
        Hoverhog.PilotInputOverride = hogFilter;
        hogHooked = true;
    }

    void UnhookHog()
    {
        if (Hoverhog.PilotInputOverride == hogFilter) Hoverhog.PilotInputOverride = null;
        hogHooked = false;
    }

    /// <summary>What the Hoverhog reads instead of the raw keys: x turn, y throttle, z climb. The same keys it reads itself
    /// (Hoverhog.Fly), with the turn lagged and wandering and the height bobbing.</summary>
    Vector3 HogInput()
    {
        float d = Drive01;
        if (Hoverhog.LocalSeat == null || UiHoldsVehicle)
        {
            UnhookHog();
            return Vector3.zero;
        }
        float now = Time.time;
        if (now == hogLastTime) return lastHogOut;
        float dt = hogLastTime < 0f ? Time.deltaTime : Mathf.Clamp(now - hogLastTime, 0.001f, 0.1f);
        hogLastTime = now;

        Vector2 raw = GameControls.DriveAxes();
        float up = 0f;
        if (GameControls.Held(KeyCode.Space, GameControls.Action.Jump)) up += 1f;
        if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.C) || GameControls.PadHeld(GameControls.Action.Flop)) up -= 1f;
        float t = now + seed * 2.3f;
        // the height bobs only while you are actually flying it (hands off = it stays put, a parked hog stays parked)
        if (raw.sqrMagnitude > 0.01f || up != 0f) up += (Mathf.Sin(t * 0.7f) * 0.7f + Mathf.Sin(t * 1.9f) * 0.3f) * 0.22f * d;
        lastHogOut = new Vector3(Steer(raw.x, d, dt, now), Throttle(raw.y, d, now), Mathf.Clamp(up, -1f, 1f));
        return lastHogOut;
    }
}
