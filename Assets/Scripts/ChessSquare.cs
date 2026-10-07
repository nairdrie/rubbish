using UnityEngine;

public class ChessSquare : MonoBehaviour
{
    // The grid coordinates of this square (e.g., a1 is (0,0))
    public Vector2Int coordinates;
    public string squareNotation;

    void Awake()
    {
        // piece notation is parent gameobject name + gameobject name
        squareNotation = transform.parent.name + gameObject.name;
        SetCoordinatesFromNotation(squareNotation);
    }

    void SetCoordinatesFromNotation(string notation)
    {
        // Convert chess notation (e.g., "a1") to grid coordinates
        int file = notation[0] - 'a'; // Convert 'a' to 0, 'b' to 1, etc.
        int rank = int.Parse(notation[1].ToString()) - 1; // Convert '1' to 0, '2' to 1, etc.
        this.coordinates = new Vector2Int(file, rank);
    }
}
