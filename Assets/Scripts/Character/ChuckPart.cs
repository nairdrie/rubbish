using UnityEngine;

/// <summary>
/// Marker on every object the Chuck builder (Setup_Character) creates, so a rebuild can find and delete the
/// previous puppet parts. <see cref="index"/> is the row in <see cref="ChuckPartsData.Parts"/>. No per-frame code.
/// </summary>
[DisallowMultipleComponent]
[AddComponentMenu("")]
public class ChuckPart : MonoBehaviour
{
    public int index = -1;
}
