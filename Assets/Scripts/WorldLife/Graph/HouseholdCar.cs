using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// WorldLife package A (Docs/WorldLife.md §3.2, §7.3): a household car parked in its house's driveway slot, placed by
/// NeighborhoodBuilder (one GameObject: the Suburbs pack car, one renderer, one BoxCollider on layer Ground).
///
/// At rest it is an ordinary batching-static scene object, exactly like a knockable (no perf change): package D hides it
/// (<see cref="body"/> and <see cref="restCollider"/> off) when the car leaves and drives a pooled copy built from
/// <see cref="prefab"/> / <see cref="tint"/> / <see cref="scale"/> / <see cref="noseYaw"/>, then shows it again when the
/// car parks back in its slot. Never move or Instantiate this object: its mesh may be the combined static batch.
/// The house's <see cref="StreetGraph.House"/> (by <see cref="stopId"/>) has the slot poses (nose toward the house) and
/// the driveway lanes; <c>House.cars</c> counts the cars that can drive.
/// </summary>
[DisallowMultipleComponent]
public sealed class HouseholdCar : MonoBehaviour
{
    public string stopId = "";
    [Tooltip("Driveway slot: 0 = StreetGraph.House.carSlot0 (deepest), 1 = carSlot1.")]
    public byte slot;
    [Tooltip("District byte (WorldLifeIds.DistrictIndex).")]
    public byte district;
    [Tooltip("The pack car prefab (SM_car_town_0x) a moving copy is built from.")]
    public GameObject prefab;
    [Tooltip("Body paint that replaces the prefab's M_car_* material (null = the prefab's own).")]
    public Material tint;
    [Tooltip("Local scale of the car (Meadows minivans are stretched; Snootington town cars and Bluffs limos are long).")]
    public Vector3 scale = Vector3.one;
    [Tooltip("Yaw (deg) from the slot pose (nose = +Z) to the model: the pack's cars point their nose along +X, so -90.")]
    public float noseYaw = -90f;
    [Tooltip("The driveway doesn't join a lane (a corner lot): this car never drives (it isn't counted in House.cars).")]
    public bool parkedOnly;
    public Renderer body;
    public BoxCollider restCollider;

    /// <summary>The slot pose of this car: where its nose points (toward the house) and where it stands.</summary>
    public Pose SlotPose => new Pose(transform.position, transform.rotation * Quaternion.Euler(0f, -noseYaw, 0f));

    // ------------------------------------------------------------------ registry (enabled cars in loaded scenes)

    static readonly List<HouseholdCar> all = new List<HouseholdCar>(128);
    public static IReadOnlyList<HouseholdCar> All => all;

    void OnEnable() => all.Add(this);
    void OnDisable() => all.Remove(this);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => all.Clear();
}
