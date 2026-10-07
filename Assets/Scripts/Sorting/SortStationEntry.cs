using UnityEngine;

/// <summary>
/// The only thing the sort station offers in third person: "Step up to the sorting table" (or "Step up with the
/// bag" / "Step up to sort it"). Exposed through an InteractionZone in front of the table + compactor whose score
/// beats the carry system's toss candidates, so E at the station always steps up (never a plain third-person toss).
/// </summary>
public class SortStationEntry : MonoBehaviour, IInteractable
{
    public SortStation station;

    SortStation Station
    {
        get
        {
            if (station == null) station = GetComponentInParent<SortStation>();
            return station;
        }
    }

    // Offered even while someone else is sorting here (co-op): the prompt then says so, and E explains.
    public bool CanInteract()
    {
        var s = Station;
        return s != null && s.isActiveAndEnabled && s.CanOfferEntry;
    }

    public string GetInteractionPrompt()
    {
        var s = Station;
        if (s != null && !Permissions.LocalCanWorkAt(s.transform.position)) return "Sorting table (not your shop)";
        return s != null ? s.EntryPrompt : "";
    }

    public void OnInteract(GameObject interactor)
    {
        var s = Station;
        if (s != null && !Permissions.LocalCanWorkAt(s.transform.position)) { Permissions.Deny(s.transform.position); return; }
        if (s != null && s.CanOfferEntry) s.TryEnterFromZone();
    }

    public void Highlight(bool highlight) { }
}
