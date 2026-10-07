using System.Collections.Generic;
using Cinemachine;
using UnityEngine;

/// <summary>
/// Controls → Field of view (Docs/UIPass.md §6.2). The setting is the on-foot FOV; every gameplay camera gets the same
/// delta on top of its <b>authored</b> FOV, cached the first time the camera is seen, so the truck's wider lens stays wider.
/// Only the local player's camera and cameras on a truck are touched (an allow-list): chess, the sort station (it adds
/// <see cref="GameSettings.FovDelta"/> itself, because it rewrites its lens every frame), the menu diorama and anything else
/// keep their lens. Cameras are found through Cinemachine's own registry (no Find*), a few times a second.
/// </summary>
public static class FovApplier
{
    static readonly Dictionary<int, float> authored = new Dictionary<int, float>();
    static readonly Dictionary<int, float> lastSet = new Dictionary<int, float>();
    static float nextScan;
    static float appliedDelta = float.NaN;
    static PlayerController seenPlayer;

    /// <summary>Gameplay cameras currently driven (tests).</summary>
    public static int Driven { get; private set; }

    /// <summary>Re-apply now (after a settings change).</summary>
    public static void Refresh()
    {
        nextScan = 0f;
        appliedDelta = float.NaN;
    }

    /// <summary>Called every frame by the settings applier; scans every 0.5 s or when the setting changed.</summary>
    public static void Tick(float unscaledNow)
    {
        float delta = GameSettings.FovDelta;
        bool changed = !Mathf.Approximately(delta, appliedDelta);
        if (!changed && unscaledNow < nextScan) return;
        nextScan = unscaledNow + 0.5f;

        var local = PlayerController.Local; // coop:allow(local) camera only: the local player's own lens
        if (local != null && local != seenPlayer && local.playerCamVCam != null)
        {
            seenPlayer = local;
            // the on-foot camera's authored FOV is the slider's "default"
            GameSettings.SetOnFootBaseFov(AuthoredFov(local.playerCamVCam));
            delta = GameSettings.FovDelta;
        }

        var core = CinemachineCore.Instance;
        int n = core != null ? core.VirtualCameraCount : 0;
        int driven = 0;
        for (int i = 0; i < n; i++)
        {
            var vc = core.GetVirtualCamera(i);
            if (vc == null || !IsGameplayCamera(vc, local)) continue;
            driven++;
            Apply(vc, delta);
        }
        Driven = driven;
        appliedDelta = delta;
    }

    static bool IsGameplayCamera(CinemachineVirtualCameraBase vc, PlayerController local)
    {
        // The FreeLook's three rig cameras follow the FreeLook's common lens; only the parent is driven.
        if (vc.ParentCamera is CinemachineFreeLook) return false;
        if (local != null && vc == local.playerCamVCam) return true;
        return vc.GetComponentInParent<TruckControllerV2>() != null;
    }

    static float AuthoredFov(CinemachineVirtualCameraBase vc)
    {
        int id = vc.GetInstanceID();
        if (authored.TryGetValue(id, out float f)) return f;
        f = CurrentFov(vc);
        authored[id] = f;
        return f;
    }

    static void Apply(CinemachineVirtualCameraBase vc, float delta)
    {
        int id = vc.GetInstanceID();
        float baseFov = AuthoredFov(vc);
        float cur = CurrentFov(vc);
        // Someone else changed the lens since we set it (a scripted zoom): adopt their value as the new authored one.
        if (lastSet.TryGetValue(id, out float mine) && !Mathf.Approximately(cur, mine) && !Mathf.Approximately(cur, baseFov))
        {
            baseFov = cur - (mine - baseFov);
            authored[id] = baseFov;
        }
        float want = Mathf.Clamp(baseFov + delta, 20f, 110f);
        if (!Mathf.Approximately(cur, want)) SetFov(vc, want);
        lastSet[id] = want;
    }

    static float CurrentFov(CinemachineVirtualCameraBase vc)
    {
        if (vc is CinemachineVirtualCamera v) return v.m_Lens.FieldOfView;
        if (vc is CinemachineFreeLook fl) return fl.m_Lens.FieldOfView;
        return vc.State.Lens.FieldOfView;
    }

    static void SetFov(CinemachineVirtualCameraBase vc, float fov)
    {
        if (vc is CinemachineVirtualCamera v)
        {
            var lens = v.m_Lens;
            lens.FieldOfView = fov;
            v.m_Lens = lens;
        }
        else if (vc is CinemachineFreeLook fl)
        {
            var lens = fl.m_Lens;
            lens.FieldOfView = fov;
            fl.m_Lens = lens;
            if (!fl.m_CommonLens)
                for (int r = 0; r < 3; r++)
                {
                    var rig = fl.GetRig(r);
                    if (rig == null) continue;
                    var rl = rig.m_Lens;
                    int rid = rig.GetInstanceID();
                    if (!authored.TryGetValue(rid, out float rb)) authored[rid] = rb = rl.FieldOfView;
                    rl.FieldOfView = Mathf.Clamp(rb + (fov - AuthoredFov(fl)), 20f, 110f);
                    rig.m_Lens = rl;
                }
        }
    }

    /// <summary>Put every driven camera back to its authored FOV (leaving play, tests).</summary>
    public static void RestoreAll()
    {
        var core = CinemachineCore.Instance;
        int n = core != null ? core.VirtualCameraCount : 0;
        for (int i = 0; i < n; i++)
        {
            var vc = core.GetVirtualCamera(i);
            if (vc == null) continue;
            if (authored.TryGetValue(vc.GetInstanceID(), out float f)) SetFov(vc, f);
        }
        Clear();
    }

    static void Clear()
    {
        authored.Clear();
        lastSet.Clear();
        seenPlayer = null;
        appliedDelta = float.NaN;
        nextScan = 0f;
        Driven = 0;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => Clear();
}
