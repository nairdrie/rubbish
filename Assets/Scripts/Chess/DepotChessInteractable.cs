using UnityEngine;

/// <summary>Forwards the chess table's existing layer-3 interaction collider to its local board facade.</summary>
[DisallowMultipleComponent]
public sealed class DepotChessInteractable : MonoBehaviour, IInteractable
{
    public DepotChessBoard board;
    public bool CanInteract() => board != null && board.CanInteract();
    public string GetInteractionPrompt() => board != null ? board.GetInteractionPrompt() : "Chess";
    public void OnInteract(GameObject interactor) { if (board != null) board.OnInteract(interactor); }
    public void Highlight(bool highlight) { if (board != null) board.Highlight(highlight); }
}
