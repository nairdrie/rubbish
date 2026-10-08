using System.Collections;
using UnityEngine;

/// <summary>
/// Drives Magnus's cart along his floor rail between its two stops: the chess board and (once he has the job,
/// magnus_brain L1, which brings the rail) the Sort Stop at the sorting table. That one straight line is the whole
/// track: he cannot go anywhere else, and in particular not to the dump pile (the old branch behind the truck is gone).
/// Put this on the cart; Magnus rides on it. The magnus_rail_motor L1 motor doubles the rail speed (0.7 -> 1.4 m/s).
///
/// It only moves the cart on the machine that calls it; MagnusPoseNet replicates the pose.
/// </summary>
public class MagnusTrack : MonoBehaviour
{
    [SerializeField] MagnusArm arm;
    [Tooltip("Where the cart parks to play chess.")]
    [SerializeField] Transform chessStop;
    [Tooltip("Optional: where the cart parks to sort at the sorting table (end of the rail branch; set by Setup_GarageUpgrades).")]
    [SerializeField] Transform sortStop;
    [Tooltip("Top speed along the rail in m/s (stock motor).")]
    [SerializeField] float speed = 0.7f;
    [Tooltip("Top speed with the magnus_rail_motor upgrade (L1+).")]
    [SerializeField] float motorSpeed = 1.4f;

    [Header("Wheels (optional)")]
    [Tooltip("Wheel pivots: each spins about its own local X (its axle).")]
    [SerializeField] Transform[] wheels;
    [SerializeField] float wheelRadius = 0.06f;
    [Tooltip("Optional: forward of this transform is the direction of travel along the rail (the cart's chassis). Empty = the cart's own forward.")]
    [SerializeField] Transform travelFrame;

    public bool IsDriving { get; private set; }

    public bool AtChess => chessStop && !IsDriving && (transform.position - chessStop.position).sqrMagnitude < 0.0004f;
    public bool AtSort => sortStop && !IsDriving && (transform.position - sortStop.position).sqrMagnitude < 0.0004f;
    public bool HasSortStop => sortStop != null;
    public Transform SortStop { get => sortStop; set => sortStop = value; }
    public Transform ChessStop => chessStop;

    // Disabling a pocket interior stops Unity coroutines, but does not clear their C# handles/flags.
    // Leave the cart where it stopped; the sorter can safely route it home when the lot wakes up.
    void OnDisable() => StopDriving();

    public void StopDriving()
    {
        StopAllCoroutines();
        IsDriving = false;
    }

    /// <summary>Rail speed right now (m/s): the stock motor, or the upgrade's.</summary>
    public float Speed => DepotMachineContext.Level(this, ShopIds.MagnusRailMotor) >= 1 ? Mathf.Max(speed, motorSpeed) : speed;

    /// <summary>Flat distance (m) from the cart to the Sort Stop (infinity without one).</summary>
    public float DistanceToSort
    {
        get
        {
            if (!sortStop) return float.PositiveInfinity;
            Vector3 d = transform.position - sortStop.position; d.y = 0f;
            return d.magnitude;
        }
    }

    public Coroutine DriveToChess() => StartCoroutine(DriveRoute(chessStop));
    public Coroutine DriveToSort() => StartCoroutine(DriveRoute(sortStop));

    /// <summary>The only places the cart may go.</summary>
    public bool IsStop(Transform stop) => stop != null && (stop == chessStop || stop == sortStop);

    /// <summary>Drive along the rail to one of its two stops (the chess board or the Sort Stop). Anything else is refused.</summary>
    public IEnumerator DriveRoute(Transform stop)
    {
        if (!IsStop(stop) || IsDriving) yield break;
        yield return DriveTo(stop);
    }

    /// <summary>The motion itself (no route check: use <see cref="DriveRoute"/> / DriveToChess / DriveToSort).</summary>
    public IEnumerator DriveTo(Transform stop)
    {
        if (!stop || IsDriving) yield break;
        IsDriving = true;
        try
        {
            Vector3 from = transform.position, to = stop.position;
            float duration = Mathf.Max(0.5f, 1.875f * Vector3.Distance(from, to) / Mathf.Max(0.05f, Speed));
            Vector3 last = from;
            for (float t = 0f; t < duration; t += Time.deltaTime)
            {
                float s = Mathf.Clamp01(t / duration);
                s = s * s * s * (10f + s * (-15f + 6f * s));   // ease in and out
                Step(Vector3.Lerp(from, to, s), ref last);
                yield return null;
            }
            Step(to, ref last);
        }
        finally { IsDriving = false; }
    }

    void Step(Vector3 position, ref Vector3 last)
    {
        transform.position = position;
        Vector3 delta = position - last;
        last = position;
        if (arm) arm.ShiftTarget(delta);   // carry the arm's pose along with the cart

        if (wheels == null || wheelRadius <= 0f) return;
        Vector3 along = travelFrame ? travelFrame.forward : transform.forward;
        float degrees = Vector3.Dot(delta, along) / wheelRadius * Mathf.Rad2Deg;
        foreach (var wheel in wheels)
            if (wheel) wheel.Rotate(Vector3.right, degrees, Space.Self);
    }

    [ContextMenu("Drive to the Sort Stop (play mode)")]
    void TestDriveToSort() { if (Application.isPlaying) DriveToSort(); }

    [ContextMenu("Drive to chess board (play mode)")]
    void TestDriveToChess() { if (Application.isPlaying) DriveToChess(); }
}
