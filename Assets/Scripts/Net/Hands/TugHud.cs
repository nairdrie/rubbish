using UnityEngine;

/// <summary>Legacy component name retained for scene compatibility. No tug HUD, beat input or movement lock.
/// Sends local walking intent for the host's shared-grip rules and provides a collision-safe soft leash.</summary>
[DefaultExecutionOrder(-110)]
public class TugHud : MonoBehaviour
{
    uint grip, sequence;
    float nextSend, nextCreak;
    PlayerBody local;
    CharacterController controller;
    StarterAssets.StarterAssetsInputs input;
    Vector3 beforeMove;
    bool stepped, focusHeld, hintHeld;
    TugNet shared;
    static readonly System.Func<PlayerController, bool> Focus = p => true;

    void OnDisable() => ReleaseFocus();

    void Update()
    {
        stepped = false;
        var net = HandsNet.Instance;
        var t = net != null && net.Active ? net.TugOf(net.LocalId) : null;
        if (!t.HasValue) { grip = 0; ReleaseFocus(); return; }
        shared = t.Value;
        local = PlayerBody.For(net.LocalId);
        if (local == null) return;
        if (grip != shared.tug)
        {
            grip = shared.tug;
            sequence = 0;
            nextSend = 0f;
            input = local.GetComponent<StarterAssets.StarterAssetsInputs>();
            controller = local.GetComponent<CharacterController>();
            Sfx.Play("pickup", net.Get(shared.item)?.Item?.WorldCenter, 0.5f, 0.8f);
        }
        TakeFocus();
        bool blocked = PlayerController.IsUIBlocking || SceneController.IsTransitioning ||
            local.Player.IsDriving || local.Player.IsPlayingChess;
        Vector3 effort = Vector3.zero;
        if (!blocked && input != null)
        {
            var cam = Camera.main;
            effort = SharedGripRules.Effort(input.move, input.sprint, cam != null ? cam.transform.eulerAngles.y : local.transform.eulerAngles.y);
        }
        if (Time.unscaledTime >= nextSend)
        {
            nextSend = Time.unscaledTime + 1f / 15f;
            net.LocalGripIntent(grip, ++sequence, effort);
        }
        if (!blocked && GameControls.Down(KeyCode.G, GameControls.Action.Drop)) net.LocalLetGo(shared.item);
        beforeMove = local.transform.position;
        stepped = !blocked && controller != null && controller.enabled;
    }

    void LateUpdate()
    {
        var net = HandsNet.Instance;
        if (grip == 0 || net == null || local == null || !net.TugOf(net.LocalId).HasValue) return;
        var other = PlayerBody.For(shared.holder == net.LocalId ? shared.challenger : shared.holder);
        if (stepped && other != null)
        {
            var correction = SharedGripRules.Tether(local.transform.position - other.transform.position,
                local.transform.position - beforeMove, Time.deltaTime);
            if (correction.sqrMagnitude > 0.000001f) controller.Move(correction);
        }
        if (!PlayerController.IsUIBlocking)
        {
            // Ordinary control hint only; strain is communicated by the bag and sound in the world.
            GameHUD.SetContextHint("Sharing a grip · Move together to carry · [G] Let go");
            hintHeld = true;
        }
        float strain = (shared.strainH + shared.strainC) / Mathf.Max(1f, shared.ripAt);
        if (strain > 0.2f && Time.unscaledTime >= nextCreak)
        {
            nextCreak = Time.unscaledTime + Mathf.Lerp(0.8f, 0.22f, strain);
            var item = net.Get(shared.item)?.Item;
            if (item != null) Sfx.Play("rustle", item.WorldCenter, Mathf.Lerp(0.25f, 0.65f, strain), Mathf.Lerp(0.85f, 1.25f, strain));
        }
    }

    void TakeFocus()
    {
        if (focusHeld || PlayerController.InteractionFocusOverride != null) return;
        PlayerController.InteractionFocusOverride = Focus;
        focusHeld = true;
    }

    void ReleaseFocus()
    {
        if (focusHeld && PlayerController.InteractionFocusOverride == Focus) PlayerController.InteractionFocusOverride = null;
        focusHeld = false;
        if (hintHeld) GameHUD.SetContextHint(null);
        hintHeld = false;
    }
}
