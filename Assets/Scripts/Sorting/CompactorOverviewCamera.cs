using Cinemachine;
using UnityEngine;

/// <summary>A local cinematic view during the forced vacuum ride, so the follow camera never enters the machine.</summary>
public sealed class CompactorOverviewCamera : MonoBehaviour
{
    public const float FieldOfView = 66f;
    static CompactorOverviewCamera current;
    static CinemachineCore.GetBlendOverrideDelegate previousBlend;
    static bool hookInstalled;
    CinemachineVirtualCamera shot;
    PlayerTells owner;
    float emergencyEnd, lowerPriorityAt = -1f, disposeAt = -1f;

    public static CompactorOverviewCamera Begin(PlayerTells player, Vector3 chamber, float seconds)
    {
        if (!Application.isPlaying || Application.isBatchMode || player == null || !player.IsLocal) return null;
        if (current != null) current.End(true);
        int lot = DepotRowLayout.InteriorAt(chamber);
        var depot = lot >= 0 ? DepotRowScene.Lot(lot) : null;
        var compactor = depot != null && depot.interior != null ? depot.interior.GetComponentInChildren<Compactor>(true) : null;
        if (compactor == null)
            foreach (var candidate in Object.FindObjectsByType<Compactor>(FindObjectsSortMode.None))
                if ((candidate.TossTargetPosition - chamber).sqrMagnitude < 9f) { compactor = candidate; break; }
        if (compactor == null) return null;
        var root = new GameObject("Compactor overview (local camera)");
        root.transform.SetParent(compactor.transform, false);
        var view = root.AddComponent<CompactorOverviewCamera>();
        view.owner = player;
        view.emergencyEnd = Time.unscaledTime + Mathf.Clamp(seconds, 0.2f, 6f) + 4f;
        var pose = OverviewPose(compactor.TossTargetPosition, compactor.transform.forward, compactor.transform.right);
        root.transform.SetPositionAndRotation(pose.position, pose.rotation);
        view.shot = root.AddComponent<CinemachineVirtualCamera>();
        view.shot.Priority = 90;
        view.shot.m_Lens.FieldOfView = FieldOfView;
        view.shot.m_Lens.NearClipPlane = 0.08f;
        view.shot.m_Lens.FarClipPlane = Camera.main != null ? Camera.main.farClipPlane : 1000f;
        view.shot.PreviousStateIsValid = false;
        current = view;
        if (!hookInstalled)
        {
            previousBlend = CinemachineCore.GetBlendOverride;
            CinemachineCore.GetBlendOverride = Blend;
            hookInstalled = true;
        }
        return view;
    }

    /// <summary>Elevated front-left view, aimed across the chamber, table, tipping floor, and airborne crew.</summary>
    public static Pose OverviewPose(Vector3 mouth, Vector3 forward, Vector3 right)
    {
        forward.y = right.y = 0f;
        forward = forward.sqrMagnitude > 0.01f ? forward.normalized : Vector3.back;
        right = right.sqrMagnitude > 0.01f ? right.normalized : Vector3.left;
        Vector3 position = mouth + forward * 6f + right * 0.8f + Vector3.up * 4.8f;
        Vector3 focus = mouth - right * 3.5f + forward * 0.6f + Vector3.up * 0.1f;
        return new Pose(position, Quaternion.LookRotation(focus - position, Vector3.up));
    }

    /// <summary>Show the resulting box briefly, then blend back without changing the player's camera/input state.</summary>
    public void End(bool immediate = false)
    {
        if (immediate)
        {
            if (shot != null) { shot.Priority = 0; shot.enabled = false; }
            Destroy(gameObject);
            return;
        }
        if (lowerPriorityAt >= 0f) return;
        lowerPriorityAt = Time.unscaledTime + 0.25f;
        disposeAt = lowerPriorityAt + 0.5f;
    }

    void Update()
    {
        if (owner == null || !owner.gameObject.activeInHierarchy) { End(true); return; }
        if (Time.unscaledTime >= emergencyEnd) End();
        if (lowerPriorityAt >= 0f && Time.unscaledTime >= lowerPriorityAt && shot != null) shot.Priority = 0;
        if (disposeAt >= 0f && Time.unscaledTime >= disposeAt) Destroy(gameObject);
    }

    static CinemachineBlendDefinition Blend(ICinemachineCamera from, ICinemachineCamera to,
        CinemachineBlendDefinition definition, MonoBehaviour brain)
    {
        if (previousBlend != null) definition = previousBlend(from, to, definition, brain);
        if (current == null || current.shot == null || !(brain is CinemachineBrain)) return definition;
        if (ReferenceEquals(to, current.shot)) return new CinemachineBlendDefinition(CinemachineBlendDefinition.Style.EaseInOut, 0.18f);
        if (ReferenceEquals(from, current.shot)) return new CinemachineBlendDefinition(CinemachineBlendDefinition.Style.EaseInOut, 0.3f);
        return definition;
    }

    void OnDisable()
    {
        if (shot != null) shot.Priority = 0;
        if (current != this) return;
        current = null;
        if (hookInstalled && CinemachineCore.GetBlendOverride == (CinemachineCore.GetBlendOverrideDelegate)Blend)
        {
            CinemachineCore.GetBlendOverride = previousBlend;
            previousBlend = null;
            hookInstalled = false;
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        if (hookInstalled && CinemachineCore.GetBlendOverride == (CinemachineCore.GetBlendOverrideDelegate)Blend)
            CinemachineCore.GetBlendOverride = previousBlend;
        current = null;
        previousBlend = null;
        hookInstalled = false;
    }
}
