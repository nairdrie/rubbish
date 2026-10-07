using System.Collections;
using UnityEngine;

/// <summary>
/// A big chunky button on the compactor (Interactable layer). SMASH crushes the hopper; CrushAll is the
/// lazy "crush EVERYTHING" option (press twice to confirm). Standing at both shows E / F choices.
/// </summary>
public class CompactorButton : MonoBehaviour, IInteractable
{
    public enum Mode { Smash, CrushAll }

    public Compactor compactor;
    public Mode mode = Mode.Smash;
    [Tooltip("The part that physically presses in (moves along pressAxis).")]
    public Transform cap;
    public float pressDepth = 0.06f;
    [Tooltip("Direction the cap moves when pressed, in the cap parent's local space.")]
    public Vector3 pressAxis = Vector3.down;

    Vector3 capRest;
    Vector3 capScale;
    bool highlighted;

    void Awake()
    {
        if (compactor == null) compactor = GetComponentInParent<Compactor>();
        if (cap != null) { capRest = cap.localPosition; capScale = cap.localScale; }
        // Refresh existing authored shops too; no scene regeneration is needed for clearer labels.
        var label = transform.Find("Label")?.GetComponent<TMPro.TextMeshPro>();
        if (label != null)
        {
            label.text = mode == Mode.Smash ? "COMPACT\nCHAMBER" : "BULK COMPACT\nSHOP";
            label.rectTransform.sizeDelta = new Vector2(0.44f, 0.13f);
            label.enableAutoSizing = true;
        }
        var plate = transform.Find("LabelPlate");
        if (plate != null) plate.localScale = new Vector3(0.46f, 0.14f, 0.01f);
    }

    // Only grab focus when there's something to crush: an idle button sits right next to the sorting table
    // and would otherwise steal E from items you're trying to pick up. With the first-person SortStation in
    // the scene the compactor is only operated from there (never from third person).
    public bool CanInteract() => compactor != null && !compactor.Busy && !SortingKit.LocalPlayerDriving
        && (SortStation.Current == null || SortStation.LocalInSortMode)
        && (mode == Mode.Smash ? compactor.HopperCount > 0 || CompactorCube.AnyPlayerIn(compactor)
            : compactor.LazyKg(out int things) > 0.01f || CompactorCube.PlayersInShop(compactor, new System.Collections.Generic.List<PlayerTells>()).Count > 0);

    /// <summary>
    /// Can the first-person sort station press it right now? (Also when there's nothing to crush: smashing
    /// air is its own reward, and CRUSH ALL says the pile's clean.)
    /// </summary>
    public bool CanPress => compactor != null && !compactor.Busy;

    public string GetInteractionPrompt()
    {
        if (compactor == null) return "";
        return mode == Mode.Smash ? compactor.SmashPrompt() : compactor.CrushAllPrompt();
    }

    public void OnInteract(GameObject interactor)
    {
        if (!CanInteract()) return;
        if (!Permissions.LocalCanWorkAt(transform.position)) { Permissions.Deny(transform.position); return; }
        Press();
    }

    /// <summary>Press the button (animation + SMASH / CRUSH ALL). Used by OnInteract and the SortStation.</summary>
    public void Press()
    {
        if (!CanPress) return;
        if (!Permissions.LocalCanWorkAt(transform.position)) { Permissions.Deny(transform.position); return; }
        StartCoroutine(PressAnim());
        Sfx.Play("click", transform.position, 1f, mode == Mode.Smash ? 0.8f : 1.2f);
        if (mode == Mode.Smash) compactor.Smash();
        else compactor.CrushAllPressed();
    }

    public void Highlight(bool highlight)
    {
        if (highlighted == highlight || cap == null) return;
        highlighted = highlight;
        cap.localScale = highlight ? capScale * 1.08f : capScale;
    }

    IEnumerator PressAnim()
    {
        if (cap == null) yield break;
        Juice.Squash(cap, 0.3f, 0.2f);
        Vector3 down = capRest + pressAxis.normalized * pressDepth;
        for (float t = 0f; t < 0.06f; t += Time.deltaTime) { cap.localPosition = Vector3.Lerp(capRest, down, t / 0.06f); yield return null; }
        cap.localPosition = down;
        yield return new WaitForSeconds(0.08f);
        for (float t = 0f; t < 0.2f; t += Time.deltaTime) { cap.localPosition = Vector3.Lerp(down, capRest, SortingKit.EaseOutBack(t / 0.2f)); yield return null; }
        cap.localPosition = capRest;
    }
}
