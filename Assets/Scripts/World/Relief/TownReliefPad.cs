using UnityEngine;

/// <summary>
/// Asks the relief bake for level ground here: a rectangle in this object's own XZ frame (a pool, a court, a pond).
/// The bake levels the land to the height it had and blends back over <see cref="feather"/> metres. Data only.
/// </summary>
[DisallowMultipleComponent]
public sealed class TownReliefPad : MonoBehaviour
{
    [Tooltip("Half extents in metres along this object's right and forward.")]
    public Vector2 half = new Vector2(5f, 5f);
    [Tooltip("Metres over which the level patch blends back into the land.")]
    public float feather = 5f;
    [Tooltip("Metres above (below, if negative) the land's own height.")]
    public float bias;

    public TownReliefDesign.Pad ToPad() => new TownReliefDesign.Pad
    {
        centre = new Vector2(transform.position.x, transform.position.z), half = half,
        yaw = transform.eulerAngles.y, feather = feather, bias = bias, name = name,
    };
}
