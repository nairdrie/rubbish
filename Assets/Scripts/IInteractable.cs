using UnityEngine;
public interface IInteractable
{
    public static KeyCode InteractKey = KeyCode.E;
    public static KeyCode AltInteractKey = KeyCode.F;  
    /// <summary> Can the player interact right now? </summary>
    bool CanInteract();

    /// <summary> Text for the “Press E to …” prompt. </summary>
    string GetInteractionPrompt();

    /// <summary> Called when the player actually presses the interact button. </summary>
    void OnInteract(GameObject interactor);

    void Highlight(bool highlight);

}