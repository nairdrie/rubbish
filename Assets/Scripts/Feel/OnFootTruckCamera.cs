using Cinemachine;
using UnityEngine;

/// <summary>Pull the walking camera in front of trucks without making bags, people, or ladder zones camera walls.
/// Leaves the existing wall solver and the separate, wide driving camera alone.</summary>
[DisallowMultipleComponent]
public sealed class OnFootTruckCamera : CinemachineExtension
{
    public const float Clearance = .30f;
    static readonly RaycastHit[] hits = new RaycastHit[64];
    PlayerController player;

    sealed class State { public float distance = -1f; }

    public static void Ensure(CinemachineVirtualCamera camera, PlayerController owner)
    {
        if (camera == null) return;
        var extension = camera.GetComponent<OnFootTruckCamera>() ?? camera.gameObject.AddComponent<OnFootTruckCamera>();
        extension.player = owner;
    }

    public static float ClearDistance(Vector3 target, Vector3 direction, float distance, TruckControllerV2 ignore = null)
    {
        if (distance < .001f) return distance;
        direction.Normalize();
        int count = Physics.SphereCastNonAlloc(target, Clearance, direction, hits, distance, ~0, QueryTriggerInteraction.Ignore);
        // A very cluttered garage must not crowd a truck out of the non-alloc buffer.
        var found = count == hits.Length
            ? Physics.SphereCastAll(target, Clearance, direction, distance, ~0, QueryTriggerInteraction.Ignore) : hits;
        if (found != hits) count = found.Length;
        float clear = distance;
        for (int i = 0; i < count; i++)
        {
            var collider = found[i].collider;
            if (collider == null || collider is WheelCollider) continue;
            var truck = collider.GetComponentInParent<TruckControllerV2>();
            if (truck == null || truck == ignore) continue;
            clear = Mathf.Min(clear, Mathf.Max(.05f, found[i].distance - .025f));
        }
        return clear;
    }

    protected override void PostPipelineStageCallback(CinemachineVirtualCameraBase camera, CinemachineCore.Stage stage,
        ref CameraState state, float deltaTime)
    {
        if (stage != CinemachineCore.Stage.Body) return;
        var memory = GetExtraState<State>(camera);
        if (player != null && player.IsDriving) { memory.distance = -1f; return; }
        if (!state.HasLookAt && camera.Follow == null) return;
        Vector3 target = state.HasLookAt ? state.ReferenceLookAt : camera.Follow.position;
        Vector3 offset = state.CorrectedPosition - target;
        float wanted = offset.magnitude;
        if (wanted < .001f) return;
        TruckControllerV2 ignore = null;
        // Riding has its own truck-space camera; never shove a mounted rider's view into their own hands.
        if (player != null && player.GetComponent<RideRider>() is RideRider rider && rider.TryGetRide(out var ride, out _))
            ignore = ride.Truck;
        float clear = ClearDistance(target, offset / wanted, wanted, ignore);
        float resolved = deltaTime < 0f || memory.distance < 0f || clear < memory.distance ? clear
            : Mathf.Lerp(memory.distance, clear, 1f - Mathf.Exp(-deltaTime / .20f));
        resolved = Mathf.Min(resolved, clear); // pull in immediately, ease out only
        memory.distance = resolved;
        state.PositionCorrection += offset / wanted * (resolved - wanted);
    }
}
