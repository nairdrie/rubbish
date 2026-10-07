using UnityEngine;

/// <summary>Time-based motion sampling avoids 30 Hz network/render jumps being divided by a 50 Hz physics step.</summary>
public sealed class VehicleMotionWindow
{
    readonly Vector3[] positions = new Vector3[40];
    readonly float[] times = new float[40];
    int head, count;
    public bool ResetOccurred { get; private set; }
    public Vector3 Sample(float time, Vector3 position)
    {
        ResetOccurred = false;
        if (!VehicleCrashRules.Finite(position) || !VehicleCrashRules.Finite(time)) { count = head = 0; ResetOccurred = true; return Vector3.zero; }
        if (count > 0)
        {
            int last = (head + positions.Length - 1) % positions.Length;
            if (time <= times[last]) return Vector3.zero;
            if ((position - positions[last]).sqrMagnitude > 144f) { count = head = 0; ResetOccurred = true; }
        }
        Vector3 velocity = Vector3.zero;
        for (int back = 1; back <= count; back++)
        {
            int index = (head + positions.Length - back) % positions.Length;
            float elapsed = time - times[index];
            if (elapsed < .2f) continue;
            velocity = elapsed <= .5f ? (position - positions[index]) / elapsed : Vector3.zero;
            break;
        }
        positions[head] = position; times[head] = time;
        head = (head + 1) % positions.Length; count = Mathf.Min(count + 1, positions.Length);
        return velocity;
    }
}
