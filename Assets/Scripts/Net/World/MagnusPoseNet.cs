using System;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>One host-written Magnus pose, tied to a specific claim of a Depot Row lot.</summary>
public struct MagnusPose : INetworkSerializable, IEquatable<MagnusPose>
{
    public byte lot, valid;
    public ulong ownerClientId;
    public int plopSerial;
    public FixedString64Bytes businessKey;
    public Vector3 cartPosition;
    public Quaternion cartRotation, baseRotation, shoulderRotation, elbowRotation, wristRotation, pointerRotation, thumbRotation;

    public void NetworkSerialize<T>(BufferSerializer<T> s) where T : IReaderWriter
    {
        s.SerializeValue(ref lot);
        s.SerializeValue(ref valid);
        s.SerializeValue(ref ownerClientId);
        s.SerializeValue(ref plopSerial);
        s.SerializeValue(ref businessKey);
        s.SerializeValue(ref cartPosition);
        s.SerializeValue(ref cartRotation);
        s.SerializeValue(ref baseRotation);
        s.SerializeValue(ref shoulderRotation);
        s.SerializeValue(ref elbowRotation);
        s.SerializeValue(ref wristRotation);
        s.SerializeValue(ref pointerRotation);
        s.SerializeValue(ref thumbRotation);
    }

    public bool Equals(MagnusPose o) => lot == o.lot && valid == o.valid && ownerClientId == o.ownerClientId &&
        plopSerial == o.plopSerial && businessKey.Equals(o.businessKey) && cartPosition.Equals(o.cartPosition) &&
        cartRotation.Equals(o.cartRotation) && baseRotation.Equals(o.baseRotation) && shoulderRotation.Equals(o.shoulderRotation) &&
        elbowRotation.Equals(o.elbowRotation) && wristRotation.Equals(o.wristRotation) &&
        pointerRotation.Equals(o.pointerRotation) && thumbRotation.Equals(o.thumbRotation);

    public override bool Equals(object obj) => obj is MagnusPose o && Equals(o);
    public override int GetHashCode() => lot ^ (plopSerial << 8) ^ ownerClientId.GetHashCode();

    public bool Matches(in DepotLotState state) => valid != 0 && lot == state.lot && state.Claimed && state.Online &&
        ownerClientId == state.ownerClientId && plopSerial == state.plopSerial && businessKey.Equals(state.businessKey);

    public static MagnusPose Blend(in MagnusPose a, in MagnusPose b, float t)
    {
        var p = b;
        p.cartPosition = Vector3.Lerp(a.cartPosition, b.cartPosition, t);
        p.cartRotation = Quaternion.Slerp(a.cartRotation, b.cartRotation, t);
        p.baseRotation = Quaternion.Slerp(a.baseRotation, b.baseRotation, t);
        p.shoulderRotation = Quaternion.Slerp(a.shoulderRotation, b.shoulderRotation, t);
        p.elbowRotation = Quaternion.Slerp(a.elbowRotation, b.elbowRotation, t);
        p.wristRotation = Quaternion.Slerp(a.wristRotation, b.wristRotation, t);
        p.pointerRotation = Quaternion.Slerp(a.pointerRotation, b.pointerRotation, t);
        p.thumbRotation = Quaternion.Slerp(a.thumbRotation, b.thumbRotation, t);
        return p;
    }
}

/// <summary>
/// Host authority samples each online depot's Magnus after its IK LateUpdate and sends a visual pose at 10 Hz.
/// Guests interpolate independently of their local solver, then apply it after that solver's LateUpdate. Chess
/// plans still advance locally; while a plan is busy, its local joints retain control so a held piece stays in claw.
/// </summary>
[NetSessionComponent(Order = 25)]
[DefaultExecutionOrder(200)]
public class MagnusPoseNet : NetworkBehaviour
{
    const float SampleSeconds = 0.1f;
    const float BlendSeconds = 0.12f;

    sealed class Bones
    {
        public DepotLot lot;
        public Transform cart, basePivot, shoulder, elbow, wrist, pointer, thumb;
        public MagnusArm arm;
        public bool Ready => cart != null && basePivot != null && shoulder != null && elbow != null && wrist != null &&
                             pointer != null && thumb != null && arm != null;
    }

    NetworkList<MagnusPose> poses;
    readonly Bones[] bones = new Bones[DepotRowLayout.LotCount];
    readonly MagnusPose[] lastTargets = new MagnusPose[DepotRowLayout.LotCount];
    readonly MagnusPose[] starts = new MagnusPose[DepotRowLayout.LotCount];
    readonly MagnusPose[] shown = new MagnusPose[DepotRowLayout.LotCount];
    readonly bool[] haveTarget = new bool[DepotRowLayout.LotCount];
    readonly float[] receivedAt = new float[DepotRowLayout.LotCount];
    float nextSample;

    void Awake() => poses = new NetworkList<MagnusPose>();

    public override void OnNetworkSpawn()
    {
        if (!IsServer) return;
        poses.Clear();
        for (int i = 0; i < DepotRowLayout.LotCount; i++) poses.Add(new MagnusPose { lot = (byte)i });
        nextSample = 0f;
    }

    void LateUpdate()
    {
        if (!IsSpawned || !DepotRowWorld.Active || DepotRowScene.Instance == null || DepotRowNet.Instance == null) return;
        if (IsServer)
        {
            if (Time.unscaledTime >= nextSample)
            {
                nextSample = Time.unscaledTime + SampleSeconds;
                SampleHost();
            }
        }
        else ApplyGuest();
    }

    void SampleHost()
    {
        var row = DepotRowNet.Instance;
        for (int i = 0; i < DepotRowLayout.LotCount && i < poses.Count; i++)
        {
            var p = new MagnusPose { lot = (byte)i };
            if (row.TryGetLot(i, out var state) && state.Claimed && state.Online)
            {
                var rig = ResolveBones(i);
                if (rig != null && rig.Ready && rig.cart.gameObject.activeInHierarchy)
                {
                    p.valid = 1;
                    p.ownerClientId = state.ownerClientId;
                    p.plopSerial = state.plopSerial;
                    p.businessKey = state.businessKey;
                    p.cartPosition = rig.cart.position;
                    p.cartRotation = rig.cart.rotation;
                    p.baseRotation = rig.basePivot.localRotation;
                    p.shoulderRotation = rig.shoulder.localRotation;
                    p.elbowRotation = rig.elbow.localRotation;
                    p.wristRotation = rig.wrist.localRotation;
                    p.pointerRotation = rig.pointer.localRotation;
                    p.thumbRotation = rig.thumb.localRotation;
                }
            }
            if (!poses[i].Equals(p)) poses[i] = p;
        }
    }

    void ApplyGuest()
    {
        var row = DepotRowNet.Instance;
        for (int i = 0; i < DepotRowLayout.LotCount && i < poses.Count; i++)
        {
            var target = poses[i];
            if (!row.TryGetLot(i, out var state) || !target.Matches(state)) { haveTarget[i] = false; continue; }
            var rig = ResolveBones(i);
            if (rig == null || !rig.Ready || !rig.cart.gameObject.activeInHierarchy) continue;

            if (!haveTarget[i] || !SameClaim(lastTargets[i], target))
            {
                haveTarget[i] = true;
                lastTargets[i] = starts[i] = shown[i] = target;
                receivedAt[i] = Time.unscaledTime - BlendSeconds;
            }
            else if (!lastTargets[i].Equals(target))
            {
                starts[i] = shown[i];
                lastTargets[i] = target;
                receivedAt[i] = Time.unscaledTime;
            }
            shown[i] = MagnusPose.Blend(starts[i], lastTargets[i], Mathf.Clamp01((Time.unscaledTime - receivedAt[i]) / BlendSeconds));
            Apply(rig, shown[i]);
        }
    }

    static bool SameClaim(in MagnusPose a, in MagnusPose b) => a.lot == b.lot && a.ownerClientId == b.ownerClientId &&
        a.plopSerial == b.plopSerial && a.businessKey.Equals(b.businessKey);

    static void Apply(Bones rig, in MagnusPose p)
    {
        Vector3 move = p.cartPosition - rig.cart.position;
        rig.cart.SetPositionAndRotation(p.cartPosition, p.cartRotation);
        // The arm's private IK target is world-space; carry it with the cart for its next local chess-plan tick.
        if (move.sqrMagnitude > 1e-8f) rig.arm.ShiftTarget(move);
        if (rig.arm.IsBusy) return;
        rig.basePivot.localRotation = p.baseRotation;
        rig.shoulder.localRotation = p.shoulderRotation;
        rig.elbow.localRotation = p.elbowRotation;
        rig.wrist.localRotation = p.wristRotation;
        rig.pointer.localRotation = p.pointerRotation;
        rig.thumb.localRotation = p.thumbRotation;
    }

    Bones ResolveBones(int i)
    {
        var lot = DepotRowScene.Lot(i);
        if (lot == null || lot.interior == null) { bones[i] = null; return null; }
        var cached = bones[i];
        if (cached != null && cached.lot == lot && cached.Ready) return cached;

        var interior = lot.interior.transform;
        var cart = interior.Find("Magnus Cart");
        var basePivot = cart != null ? cart.Find("Magnus/BasePivot") : null;
        var shoulder = basePivot != null ? basePivot.Find("Shoulder") : null;
        var elbow = shoulder != null ? shoulder.Find("Elbow") : null;
        var wrist = elbow != null ? elbow.Find("Wrist") : null;
        var rig = new Bones
        {
            lot = lot, cart = cart, basePivot = basePivot, shoulder = shoulder, elbow = elbow, wrist = wrist,
            pointer = wrist != null ? wrist.Find("Pointer") : null,
            thumb = wrist != null ? wrist.Find("Thumb") : null,
            arm = cart != null ? cart.GetComponentInChildren<MagnusArm>(true) : null,
        };
        bones[i] = rig;
        return rig;
    }
}
