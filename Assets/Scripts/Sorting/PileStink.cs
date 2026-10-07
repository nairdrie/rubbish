using System.Collections.Generic;
using UnityEngine;

/// <summary>Keeps the tipping-floor stink going while anything is left on the pile, then removes itself.</summary>
public class PileStink : MonoBehaviour
{
    Vector3 center, size;
    float nextCheck;
    readonly List<Carryable> scratch = new List<Carryable>();

    public void Init(Vector3 c, Vector3 s) { center = c; size = s; nextCheck = Time.time + 3f; }

    void Update()
    {
        if (Time.time < nextCheck) return;
        nextCheck = Time.time + 2f;
        SortingKit.CarryablesInBox(center + Vector3.up * size.y * 0.5f, size * 0.5f, Quaternion.identity, scratch);
        if (scratch.Count == 0) Destroy(gameObject);
    }
}
