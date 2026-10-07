using UnityEngine;

public struct InteractionCandidate
{
    public IInteractable Target;
    public float Score;          // higher wins
    public Collider HitCollider; // optional (for highlight routing if you want)
}

public interface IInteractionProvider
{
    void CollectCandidates(PlayerController player, System.Collections.Generic.List<InteractionCandidate> results);
}