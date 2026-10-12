using System.Collections.Generic;

/// <summary>
/// Marker: this interactable always owns the alternative key (<see cref="IInteractable.AltInteractKey"/>, F; gamepad Y),
/// whatever else is in range. The garage door is the only one: its key has to be learnable ("F is always the door"),
/// so it can't slide between E and F depending on which bag, truck or display piece happens to outscore it.
/// </summary>
public interface IAltKeyPinned : IInteractable { }

/// <summary>
/// Hands the two interaction keys out to a score-sorted option list (best first), the way PlayerController presses them:
/// <list type="bullet">
/// <item>No pinned option in range: E = the best option, F = the runner-up (the original two-button behaviour).</item>
/// <item>A pinned option in range (the best-scoring one if several): F is always it; E = the best option that is not pinned.</item>
/// </list>
/// </summary>
public static class InteractionKeys
{
    public static void Resolve(List<IInteractable> options, out IInteractable primary, out IInteractable alt)
    {
        primary = null;
        alt = null;
        if (options == null || options.Count == 0) return;

        IInteractable pinned = null;
        for (int i = 0; i < options.Count; i++)
            if (options[i] is IAltKeyPinned) { pinned = options[i]; break; }

        if (pinned == null)
        {
            primary = options[0];
            if (options.Count > 1) alt = options[1];
            return;
        }

        alt = pinned;
        for (int i = 0; i < options.Count; i++)
            if (!(options[i] is IAltKeyPinned)) { primary = options[i]; break; }
    }
}
