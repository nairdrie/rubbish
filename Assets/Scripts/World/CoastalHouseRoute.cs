using UnityEngine;

/// <summary>Authored world-space household anchors after a coastal layout relocation. No runtime state.</summary>
[DisallowMultipleComponent]
public sealed class CoastalHouseRoute : MonoBehaviour
{
    public Vector3 door, pathEnd, mailbox, ramp, top;
    public Vector3[] bends;
    public Pose car0, car1, butlerPost;
    public byte size, cars;
    public bool hasDrive;
    public Transform buildingRoot;
}
