using StarterAssets;
using UnityEngine;

/// <summary>
/// A short owner-authored vacuum ride. The ordinary owner NetworkTransform shows the same trip to other peers;
/// remote copies only pose the skeleton. No RPC can request a pull: only the host machine's accepted batch starts one.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(900)]
public sealed class CompactorPlayerPull : MonoBehaviour
{
    PlayerTells tells;
    PlayerController player;
    PlayerBody body;
    RideRider rider;
    ThirdPersonController movement;
    CharacterController controller;
    TrashCarrier carrier;
    StarterAssetsInputs input;
    CompactorOverviewCamera overview;
    Transform skeleton;
    Quaternion skeletonRotation;
    Vector3 skeletonPosition, start, chamber, landing;
    uint token;
    double started, arriveAt;
    float emergencyReleaseAt;
    bool bodyEnabled, riderEnabled, movementEnabled, playerEnabled, controllerEnabled;
    bool owner, cleaned;
    public bool IsActive { get; private set; }

    public static void Begin(PlayerTells p, uint sequence, Vector3 at, Vector3 popOut, double end)
    {
        if (p == null || sequence == 0) return;
        var existing = p.GetComponent<CompactorPlayerPull>();
        if (existing != null && existing.IsActive) return;
        var pull = existing != null ? existing : p.gameObject.AddComponent<CompactorPlayerPull>();
        pull.enabled = true;
        pull.StartPull(p, sequence, at, popOut, end);
    }

    public static void Finish(PlayerTells p, uint sequence, Vector3 at, bool cubed)
    {
        if (p == null) return;
        var pull = p.GetComponent<CompactorPlayerPull>();
        if (pull == null || !pull.IsActive || pull.token != sequence) return;
        pull.EndPull(at, cubed);
    }

    public static void Cancel(PlayerTells p, uint sequence)
    {
        var pull = p != null ? p.GetComponent<CompactorPlayerPull>() : null;
        if (pull != null && pull.IsActive && pull.token == sequence) pull.EndPull(pull.landing, false);
    }

    void StartPull(PlayerTells p, uint sequence, Vector3 at, Vector3 popOut, double end)
    {
        tells = p;
        player = p.Player;
        owner = p.IsLocal;
        body = GetComponent<PlayerBody>();
        rider = GetComponent<RideRider>();
        movement = GetComponent<ThirdPersonController>();
        controller = GetComponent<CharacterController>();
        carrier = GetComponent<TrashCarrier>();
        input = GetComponent<StarterAssetsInputs>();
        if (owner) tells.LeaveActivitiesForCompactor();
        body?.ResetForCompactor();
        rider?.ResetForCompactor();
        bodyEnabled = body != null && body.enabled;
        riderEnabled = rider != null && rider.enabled;
        if (body != null) body.enabled = false;
        if (rider != null) rider.enabled = false;
        movementEnabled = movement != null && movement.enabled;
        playerEnabled = player != null && player.enabled;
        controllerEnabled = controller != null && controller.enabled;
        if (owner)
        {
            if (movement != null) movement.enabled = false;
            if (player != null) player.enabled = false;
            if (controller != null) controller.enabled = false;
            if (carrier != null) carrier.SetExternalControl(true);
            ClearInput();
            GameHUD.Toast("THE SHOP VACUUM GOT YOU!", ToastStyle.Rare, 2f);
            Juice.Shake(0.12f, 0.22f);
        }
        skeleton = transform.Find("Skeleton");
        if (skeleton != null) { skeletonRotation = skeleton.localRotation; skeletonPosition = skeleton.localPosition; }
        token = sequence;
        start = transform.position;
        chamber = at;
        landing = popOut;
        started = CompactorCube.Now;
        arriveAt = System.Math.Max(started + 0.05, end);
        if (owner) overview = CompactorOverviewCamera.Begin(tells, chamber, (float)(arriveAt - started));
        emergencyReleaseAt = Time.unscaledTime + (float)(arriveAt - started) + 4f;
        cleaned = false;
        IsActive = true;
    }

    /// <summary>Continuous eased pull: exact endpoints, lift above clutter, diminishing corkscrew towards the throat.</summary>
    public static Vector3 PositionAlong(Vector3 from, Vector3 to, float progress, float side)
    {
        float t = Mathf.Clamp01(progress);
        float eased = t * t * (3f - 2f * t);
        float arch = Mathf.Sin(t * Mathf.PI);
        Vector3 across = Vector3.Cross(Vector3.up, to - from).normalized;
        return Vector3.Lerp(from, to, eased) + Vector3.up * (arch * 1.4f) +
            across * (Mathf.Sin(t * Mathf.PI * 4f) * arch * Mathf.Clamp(side, -1f, 1f) * 0.4f);
    }

    void LateUpdate()
    {
        if (!IsActive) return;
        if (Time.unscaledTime >= emergencyReleaseAt) { EndPull(landing, false); return; }
        float progress = Mathf.Clamp01((float)((CompactorCube.Now - started) / (arriveAt - started)));
        if (owner)
        {
            ClearInput();
            if (controller != null) controller.enabled = false;
            if (movement != null) movement.enabled = false;
            transform.position = PositionAlong(start, chamber, progress, tells.ClientId % 2 == 0 ? 1f : -1f);
        }
        if (skeleton != null)
        {
            float flop = Mathf.Sin(progress * Mathf.PI) * 65f;
            skeleton.localRotation = Quaternion.Euler(-flop, progress * 540f, Mathf.Sin(progress * 28f) * 12f) * skeletonRotation;
            skeleton.localPosition = skeletonPosition + Vector3.up * (Mathf.Sin(progress * Mathf.PI) * 0.12f);
        }
    }

    void ClearInput()
    {
        if (input == null) return;
        input.move = Vector2.zero;
        input.jump = false;
        input.sprint = false;
    }

    void EndPull(Vector3 at, bool cubed)
    {
        Cleanup();
        if (owner && tells != null)
        {
            if (cubed) tells.PopOutAsCube(at);
            else tells.PlaceOutsideCompactor(at);
            RestoreHeldMove();
        }
    }

    void OnDisable()
    {
        if (IsActive) EndPull(landing, false);
        else Cleanup();
    }

    void RestoreHeldMove()
    {
        // Input actions only send a new OnMove when the key/stick changes. We cleared it during the ride,
        // so read the live action now rather than forcing somebody still holding W to release/re-press it.
        if (input == null || PlayerController.IsUIBlocking) return;
        var pi = GetComponent<UnityEngine.InputSystem.PlayerInput>();
        if (pi == null || !pi.isActiveAndEnabled || !pi.inputIsActive || pi.actions == null) return;
        var action = pi.actions.FindAction("Move", false);
        if (action != null && action.enabled) input.move = action.ReadValue<Vector2>();
    }

    void Cleanup()
    {
        if (cleaned || !IsActive) return;
        cleaned = true;
        IsActive = false;
        if (skeleton != null) { skeleton.localRotation = skeletonRotation; skeleton.localPosition = skeletonPosition; }
        body?.ResetForCompactor();
        rider?.ResetForCompactor();
        if (body != null) body.enabled = bodyEnabled;
        if (rider != null) rider.enabled = riderEnabled;
        if (!owner) return;
        if (overview != null) { overview.End(); overview = null; }
        if (carrier != null) carrier.SetExternalControl(false);
        if (player != null) player.enabled = playerEnabled;
        if (controller != null) controller.enabled = controllerEnabled;
        if (movement != null) movement.enabled = movementEnabled;
        ClearInput();
    }
}
