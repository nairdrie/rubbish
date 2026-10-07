using StarterAssets;
using UnityEngine;

/// <summary>Cosmetic sitting; move/jump/interact stands up, and a punch immediately interrupts the break.</summary>
[DefaultExecutionOrder(100)]
public sealed class WorkshopLounge : MonoBehaviour
{
    PlayerController player;
    PlayerBody body;
    WorkshopGadget sofa;
    Transform seat;
    ThirdPersonController movement;
    CharacterController controller;
    Animator animator;
    bool wasMovement, wasController, active;
    float entered, nextLeave;
    readonly Transform[] legs = new Transform[4];
    readonly Quaternion[] legRest = new Quaternion[4];
    static readonly HumanBodyBones[] LegBones = { HumanBodyBones.LeftUpperLeg, HumanBodyBones.RightUpperLeg,
        HumanBodyBones.LeftLowerLeg, HumanBodyBones.RightLowerLeg };
    static readonly Vector3[] LegAngles = { new Vector3(-78, 0, -8), new Vector3(-78, 0, 8),
        new Vector3(82, 0, 0), new Vector3(82, 0, 0) };
    bool Local => CoopPlayerHooks.IsLocalOwner(player);
    BodyState State => body != null ? body.State : BodyState.Normal;
    public bool IsSeatedOn(WorkshopGadget value) => active && sofa == value;
    public static void Sit(PlayerController p, WorkshopGadget sofa, Transform seat)
    {
        if (p == null || sofa == null || seat == null) return;
        var lounge = p.GetComponent<WorkshopLounge>() ?? p.gameObject.AddComponent<WorkshopLounge>();
        if (lounge.IsSeatedOn(sofa)) return; // State snapshots are idempotent, not another sit/stand cycle.
        lounge.Stand(); lounge.player = p; lounge.sofa = sofa; lounge.seat = seat;
        lounge.body = p.GetComponent<PlayerBody>();
        lounge.movement = p.GetComponent<ThirdPersonController>(); lounge.controller = p.GetComponent<CharacterController>();
        lounge.animator = p.GetComponentInChildren<Animator>();
        lounge.wasMovement = lounge.movement != null && lounge.movement.enabled;
        lounge.wasController = lounge.controller != null && lounge.controller.enabled;
        lounge.entered = Time.unscaledTime; lounge.active = true;
        lounge.nextLeave = 0f;
        for (int i = 0; i < lounge.legs.Length; i++)
        {
            lounge.legs[i] = lounge.animator != null && lounge.animator.isHuman ? lounge.animator.GetBoneTransform(LegBones[i]) : null;
            if (lounge.legs[i] != null) lounge.legRest[i] = lounge.legs[i].localRotation;
        }
        if (lounge.Local) GameHUD.Toast("On break — move, jump or interact to stand", ToastStyle.Info, 3f);
    }
    void LateUpdate()
    {
        if (!active) return;
        if (player == null || sofa == null || !sofa.isActiveAndEnabled || seat == null || State != BodyState.Normal ||
            player.IsDriving || player.IsPlayingChess || (body != null && !body.enabled) ||
            CompactorCube.IsSuctioning(player.OwnerClientId) || CompactorCube.IsCubed(player.OwnerClientId))
        { Stand(); return; }
        if (Local && Time.unscaledTime - entered > .35f && !PlayerController.IsUIBlocking)
        {
            var input = player.GetComponent<StarterAssetsInputs>();
            if ((input != null && (input.move.sqrMagnitude > .01f || input.jump)) || GameControls.Down(KeyCode.Space, GameControls.Action.Jump) ||
                GameControls.Down(KeyCode.E, GameControls.Action.Interact) || GameControls.Down(KeyCode.Mouse1, GameControls.Action.Shove))
            {
                if (Time.unscaledTime >= nextLeave)
                {
                    nextLeave = Time.unscaledTime + .3f;
                    HandsNet.Instance?.LeaveWorkshopLounge(sofa.model.lot);
                }
                return;
            }
        }
        if (Local)
        {
            if (movement != null) movement.enabled = false;
            if (controller != null) controller.enabled = false;
            // Player root is at their feet; the hips sit approximately 0.9m above it.
            transform.SetPositionAndRotation(seat.position - Vector3.up * .9f, seat.rotation);
        }
        for (int i = 0; i < legs.Length; i++)
            if (legs[i] != null) legs[i].localRotation = SeatedRotation(legRest[i], i);
    }
    // Absolute from the captured pose: an idle/culled Animator must never accumulate 78 degrees every frame.
    public static Quaternion SeatedRotation(Quaternion rest, int leg) => rest * Quaternion.Euler(LegAngles[Mathf.Clamp(leg, 0, 3)]);
    public static bool RestoreControllerFor(BodyState state) => state != BodyState.Carried && state != BodyState.Riding;
    public void Stand()
    {
        if (!active) return; active = false;
        for (int i = 0; i < legs.Length; i++)
            if (legs[i] != null && Quaternion.Angle(legs[i].localRotation, SeatedRotation(legRest[i], i)) < .1f)
                legs[i].localRotation = legRest[i];
        if (player != null && Local)
        {
            bool external = player.IsDriving || player.IsPlayingChess || (body != null && !body.enabled) ||
                CompactorCube.IsSuctioning(player.OwnerClientId) || CompactorCube.IsCubed(player.OwnerClientId);
            if (!external)
            {
                if (sofa != null && State == BodyState.Normal) transform.position = sofa.ActionPosition;
                // Knockback needs the controller, but riding/carrying and other activities own their locks.
                if (controller != null && RestoreControllerFor(State)) controller.enabled = wasController;
                if (movement != null && State == BodyState.Normal) movement.enabled = wasMovement;
            }
        }
    }
    void OnDisable() => Stand();
}
