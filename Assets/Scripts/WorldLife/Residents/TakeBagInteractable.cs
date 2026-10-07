using UnityEngine;

/// <summary>
/// WorldLife package C (Docs/WorldLife.md §7.2): the "[E] Take bag" prompt on a resident carrying their bag out. Lives on
/// every pooled puppet next to an <see cref="InteractionZone"/> (on foot, ~1.6 m around them) that the director switches
/// on only while the walk-out can hand over (carrying, still walking to the curb, host or solo). The bag spawns in the
/// interacting player's hands from the same curb record (same bagId) and pays like any other in the hopper. In M2 a
/// guest's hand-over goes through the normal RequestGrab(bagId), because it's still a curb record.
/// </summary>
[DisallowMultipleComponent]
public sealed class TakeBagInteractable : MonoBehaviour, IInteractable
{
    public const string Prompt = "Take bag";

    [System.NonSerialized] public ResidentAgent agent;

    WalkOutPresenter.Job Job => agent != null ? agent.ownerRef as WalkOutPresenter.Job : null;

    public bool CanInteract()
    {
        var dir = ResidentDirector.Instance;
        if (dir == null || dir.WalkOuts == null) return false;
        var c = TrashCarrier.Local;   // the prompt is HUD for this machine's player (rule 8); OnInteract uses the interactor
        return dir.WalkOuts.CanHandOver(Job) && (c == null || !c.IsCarrying);
    }

    public string GetInteractionPrompt() => Prompt;

    public void OnInteract(GameObject interactor)
    {
        var dir = ResidentDirector.Instance;
        if (dir == null || interactor == null) return;
        var carrier = interactor.GetComponent<TrashCarrier>();
        if (carrier == null) carrier = interactor.GetComponentInChildren<TrashCarrier>();
        dir.WalkOuts.HandOver(Job, carrier);
    }

    public void Highlight(bool highlight) { }
}
