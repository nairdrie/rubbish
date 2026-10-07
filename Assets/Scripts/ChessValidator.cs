using UnityEngine;

// A simple struct to hold piece information for the validator.
public struct PieceState
{
    public PieceType Type;
    public PieceColor Color;
    public bool HasMoved; // Important for castling and pawn's first move

    public PieceState(PieceType type, PieceColor color, bool hasMoved = false)
    {
        Type = type;
        Color = color;
        HasMoved = hasMoved;
    }
}

public static class ChessValidator
{
    // Checkmate detection calls IsValidMove thousands of times per move, so per-call logging is off by default.
    public static bool LogValidation = false;

    // The main public method. It takes the board state and a move, and returns if it's legal.
    public static bool IsValidMove(PieceState[,] board, Vector2Int from, Vector2Int to, PieceColor playerColor, Vector2Int enPassantTargetSquare)
    {
        if (LogValidation)
        {
            Debug.Log($"Validating move from {from} to {to} for {playerColor}");
            Debug.Log($"Piece at from: {board[from.x, from.y].Type}, Color: {board[from.x, from.y].Color}");
            Debug.Log($"Piece at to: {board[to.x, to.y].Type}, Color: {board[to.x, to.y].Color}");
        }
        // Basic checks
        if (from == to) return false;
        if (board[from.x, from.y].Type == PieceType.None) return false;
        if (board[from.x, from.y].Color != playerColor) return false;

        // Check if the target square has a friendly piece
        if (board[to.x, to.y].Type != PieceType.None && board[to.x, to.y].Color == playerColor)
        {
            return false;
        }

        bool isPieceRuleValid = false;
        // Check piece-specific rules
        switch (board[from.x, from.y].Type)
        {
            case PieceType.Pawn:
                isPieceRuleValid = IsValidPawnMove(board, from, to, enPassantTargetSquare);
                break;
            case PieceType.Rook:
                isPieceRuleValid = IsValidStraightMove(board, from, to);
                break;
            case PieceType.Knight:
                isPieceRuleValid = IsValidKnightMove(from, to);
                break;
            case PieceType.Bishop:
                isPieceRuleValid = IsValidDiagonalMove(board, from, to);
                break;
            case PieceType.Queen:
                isPieceRuleValid = IsValidStraightMove(board, from, to) || IsValidDiagonalMove(board, from, to);
                break;
            case PieceType.King:
                isPieceRuleValid = IsValidKingMove(board, from, to, playerColor);
                break;
        }

        if (!isPieceRuleValid)
        {
            return false;
        }

        // --- Final Check: Does the move leave the king in check? ---
        PieceState[,] boardAfterMove = SimulateMove(board, from, to, enPassantTargetSquare);
        Vector2Int kingPos = FindKing(boardAfterMove, playerColor);
        if (kingPos.x == -1) // Should not happen if board is valid
        {
            Debug.LogError("King not found after simulated move!");
            return false;
        }

        if (IsSquareAttacked(boardAfterMove, kingPos, playerColor))
        {
            if (LogValidation) Debug.LogWarning("Move is illegal because it leaves the king in check.");
            return false; // Move is illegal because it leaves king in check
        }

        return true;
    }

    // --- Piece-Specific Validation Logic ---

    private static bool IsValidPawnMove(PieceState[,] board, Vector2Int from, Vector2Int to, Vector2Int enPassantTargetSquare)
    {
        PieceState pawn = board[from.x, from.y];
        int direction = (pawn.Color == PieceColor.White) ? 1 : -1;

        // Standard one-square push
        if (from.x == to.x && to.y == from.y + direction && board[to.x, to.y].Type == PieceType.None)
        {
            return true;
        }

        // Two-square initial push
        if (from.x == to.x && !pawn.HasMoved && to.y == from.y + (2 * direction) && board[to.x, to.y].Type == PieceType.None && board[from.x, from.y + direction].Type == PieceType.None)
        {
            return true;
        }

        // Capture
        if (Mathf.Abs(from.x - to.x) == 1 && to.y == from.y + direction && board[to.x, to.y].Type != PieceType.None && board[to.x, to.y].Color != pawn.Color)
        {
            return true;
        }

        // En passant
        if (to == enPassantTargetSquare && Mathf.Abs(from.x - to.x) == 1 && to.y == from.y + direction)
        {
            return true;
        }

        return false;
    }

    private static bool IsValidKnightMove(Vector2Int from, Vector2Int to)
    {
        int dx = Mathf.Abs(from.x - to.x);
        int dy = Mathf.Abs(from.y - to.y);
        return (dx == 1 && dy == 2) || (dx == 2 && dy == 1);
    }

    private static bool IsValidKingMove(PieceState[,] board, Vector2Int from, Vector2Int to, PieceColor playerColor)
    {
        // Standard king move
        int dx = Mathf.Abs(from.x - to.x);
        int dy = Mathf.Abs(from.y - to.y);
        if (dx <= 1 && dy <= 1)
        {
            return true;
        }

        // --- Castling Logic ---
        // It must be a castling move (king moves 2 squares horizontally).
        if (dy != 0 || dx != 2) return false;

        // King must not have moved.
        if (board[from.x, from.y].HasMoved) return false;

        // Determine if it's kingside or queenside castling.
        int rookFile = (to.x > from.x) ? 7 : 0; // Kingside (h-file) or Queenside (a-file)
        int rookRank = from.y;
        Vector2Int rookPos = new Vector2Int(rookFile, rookRank);

        // Check if there's a rook in the corner and if it has moved.
        if (board[rookPos.x, rookPos.y].Type != PieceType.Rook || board[rookPos.x, rookPos.y].HasMoved)
        {
            return false;
        }

        // Check for pieces between king and rook.
        int step = (to.x > from.x) ? 1 : -1;
        for (int x = from.x + step; x != rookFile; x += step)
        {
            if (board[x, from.y].Type != PieceType.None)
            {
                return false;
            }
        }

        // Check if king is in check, or passes through or lands on a square under attack.
        if (IsSquareAttacked(board, from, playerColor)) return false; // King is currently in check
        if (IsSquareAttacked(board, new Vector2Int(from.x + step, from.y), playerColor)) return false; // Pass-through square
        if (IsSquareAttacked(board, to, playerColor)) return false; // Destination square

        return true;
    }

    private static bool IsValidStraightMove(PieceState[,] board, Vector2Int from, Vector2Int to)
    {
        if (from.x != to.x && from.y != to.y) return false; // Not a straight line

        int dx = (to.x > from.x) ? 1 : (to.x < from.x) ? -1 : 0;
        int dy = (to.y > from.y) ? 1 : (to.y < from.y) ? -1 : 0;

        int x = from.x + dx;
        int y = from.y + dy;

        while (x != to.x || y != to.y)
        {
            if (board[x, y].Type != PieceType.None) return false; // Path is blocked
            x += dx;
            y += dy;
        }
        return true;
    }

    private static bool IsValidDiagonalMove(PieceState[,] board, Vector2Int from, Vector2Int to)
    {
        if (Mathf.Abs(from.x - to.x) != Mathf.Abs(from.y - to.y)) return false; // Not diagonal

        int dx = (to.x > from.x) ? 1 : -1;
        int dy = (to.y > from.y) ? 1 : -1;

        int x = from.x + dx;
        int y = from.y + dy;

        while (x != to.x && y != to.y)
        {
            if (board[x, y].Type != PieceType.None) return false; // Path is blocked
            x += dx;
            y += dy;
        }
        return true;
    }

    /// <summary>
    /// Checks if a given square is under attack by the opponent.
    /// </summary>
    /// <param name="board">The current board state.</param>
    /// <param name="square">The square to check.</param>
    /// <param name="playerColor">The color of the player whose safety is being checked (NOT the attacker's color).</param>
    /// <returns>True if the square is attacked, false otherwise.</returns>
    public static bool IsSquareAttacked(PieceState[,] board, Vector2Int square, PieceColor playerColor)
    {
        PieceColor attackerColor = (playerColor == PieceColor.White) ? PieceColor.Black : PieceColor.White;

        for (int file = 0; file < 8; file++)
        {
            for (int rank = 0; rank < 8; rank++)
            {
                Vector2Int startSquare = new Vector2Int(file, rank);
                PieceState piece = board[startSquare.x, startSquare.y];

                if (piece.Type == PieceType.None || piece.Color != attackerColor)
                {
                    continue;
                }

                // Check if this piece can attack the target 'square'
                bool canAttack = false;
                switch (piece.Type)
                {
                    case PieceType.Pawn:
                        int direction = (piece.Color == PieceColor.White) ? 1 : -1;
                        if (Mathf.Abs(startSquare.x - square.x) == 1 && square.y == startSquare.y + direction)
                        {
                            canAttack = true;
                        }
                        break;
                    case PieceType.Knight:
                        canAttack = IsValidKnightMove(startSquare, square);
                        break;
                    case PieceType.Bishop:
                        canAttack = IsValidDiagonalMove(board, startSquare, square);
                        break;
                    case PieceType.Rook:
                        canAttack = IsValidStraightMove(board, startSquare, square);
                        break;
                    case PieceType.Queen:
                        canAttack = IsValidDiagonalMove(board, startSquare, square) || IsValidStraightMove(board, startSquare, square);
                        break;
                    case PieceType.King:
                        int dx = Mathf.Abs(startSquare.x - square.x);
                        int dy = Mathf.Abs(startSquare.y - square.y);
                        if (dx <= 1 && dy <= 1) canAttack = true;
                        break;
                }

                if (canAttack) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Creates a new board state by applying a given move.
    /// </summary>
    private static PieceState[,] SimulateMove(PieceState[,] board, Vector2Int from, Vector2Int to, Vector2Int enPassantTargetSquare)
    {
        PieceState[,] boardCopy = (PieceState[,])board.Clone();
        PieceState movingPiece = boardCopy[from.x, from.y];

        // Standard move
        boardCopy[to.x, to.y] = movingPiece;
        boardCopy[from.x, from.y] = new PieceState(); // Empty the original square

        // Handle special move side-effects for the simulation
        if (movingPiece.Type == PieceType.King && Mathf.Abs(from.x - to.x) == 2) // Castling
        {
            // Move the rook as well
            int rookFile = (to.x > from.x) ? 7 : 0;
            int rookNewFile = (to.x > from.x) ? 5 : 3;
            boardCopy[rookNewFile, from.y] = boardCopy[rookFile, from.y];
            boardCopy[rookFile, from.y] = new PieceState();
        }
        else if (movingPiece.Type == PieceType.Pawn && to == enPassantTargetSquare)
        {
            // This was an en passant capture, so we need to remove the captured pawn.
            // The captured pawn is on the same rank as the moving pawn's start, and same file as its end.
            int capturedPawnRank = from.y;
            int capturedPawnFile = to.x;
            boardCopy[capturedPawnFile, capturedPawnRank] = new PieceState();
        }

        return boardCopy;
    }

    /// <summary>
    /// Finds the position of the king for a given color.
    /// </summary>
    /// <returns>The king's coordinates, or (-1, -1) if not found.</returns>
    private static Vector2Int FindKing(PieceState[,] board, PieceColor kingColor)
    {
        for (int file = 0; file < 8; file++)
        {
            for (int rank = 0; rank < 8; rank++)
            {
                PieceState piece = board[file, rank];
                if (piece.Type == PieceType.King && piece.Color == kingColor)
                {
                    return new Vector2Int(file, rank);
                }
            }
        }
        return new Vector2Int(-1, -1); // King not found
    }
}
