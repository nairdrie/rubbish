using UnityEngine;

/// <summary>
/// Proxy target for the lever's InteractionZone: only offers "TIP IT!" (no aiming needed) when the truck is
/// actually ready, so standing near the lever with an empty truck doesn't steal E from grabbing bags.
/// </summary>
public class TruckUnloaderZoneTarget : MonoBehaviour, IInteractable
{
    public TruckUnloader unloader;

    public bool CanInteract() => unloader != null && unloader.IsReady && unloader.CanInteract();
    public string GetInteractionPrompt() => unloader != null ? unloader.GetInteractionPrompt() : "";
    public void OnInteract(GameObject interactor) { if (unloader != null) unloader.OnInteract(interactor); }
    public void Highlight(bool highlight) { }
}
