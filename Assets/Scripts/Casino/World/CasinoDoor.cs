using UnityEngine;

/// <summary>
/// A door of The Lucky Raccoon: the street door (<see cref="exit"/> false, on the runtime front) or the EXIT door inside
/// (true, in <c>Casino.unity</c>). Like every door it is <see cref="IAltKeyPinned"/>: always on F, whatever else is in
/// range. Found through an <see cref="InteractionZone"/>; moves only the player who uses it (<see cref="CasinoWorld"/>).
/// </summary>
public class CasinoDoor : MonoBehaviour, IInteractable, IAltKeyPinned
{
    [Tooltip("True for the door inside the casino (leads out to the street).")]
    public bool exit;
    public string promptEnter = "Enter The Lucky Raccoon";
    public string promptExit = "Leave";
    public string promptHandsFull = "Hands full! (G to drop)";

    public bool CanInteract()
    {
        if (CasinoWorld.Transitioning || DepotRowWorld.Transitioning || SceneController.IsTransitioning) return false;
        if (DrunkStatus.PassingOut) return false; // keeling over: the pass-out owns the fade and the trip home
        return CasinoStation.LocalBusy == null;
    }

    public string GetInteractionPrompt()
    {
        var carrier = TrashCarrier.Local;
        if (carrier != null && carrier.IsCarrying) return promptHandsFull;
        return exit ? promptExit : promptEnter;
    }

    public void OnInteract(GameObject interactor)
    {
        if (!CanInteract()) return;
        if (exit) CasinoWorld.Exit(interactor);
        else CasinoWorld.Enter(interactor);
    }

    public void Highlight(bool highlight) { }
}
