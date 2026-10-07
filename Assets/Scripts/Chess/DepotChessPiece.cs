using UnityEngine;

/// <summary>A visual chess piece in a Depot Row pocket interior. Its index is stable across all four copied boards.</summary>
[DisallowMultipleComponent]
public sealed class DepotChessPiece : MonoBehaviour
{
    public byte index;
    public PieceColor color;
    public PieceType startingType;
    [HideInInspector] public PieceType type;
    [HideInInspector] public Vector3 originalPosition;
}
