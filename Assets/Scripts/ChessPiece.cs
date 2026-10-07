using UnityEngine;
using Unity.Netcode;

public enum PieceColor { White, Black }
public enum PieceType { None, Pawn, Rook, Knight, Bishop, Queen, King }

public class ChessPiece : NetworkBehaviour
{
    public PieceColor Color;
    public PieceType Type;
    public NetworkVariable<bool> HasMoved = new(false);

    // We'll use this to snap the piece back if a move is invalid.
    [HideInInspector] public Vector3 OriginalPosition;
}
