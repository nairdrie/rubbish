using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// WorldLife package A (Docs/WorldLife.md §3.3, §5.4): the trigger inside a town-exit tunnel (Old Highway, Interstate
/// On-Ramp, Private Airport Rd), placed by NeighborhoodBuilder. Household cars drive into the dark and fade out (package D
/// uses <c>StreetGraph.exits[i].portal</c>); a truck that drives in bonks the wall collider 6 m inside, and the truck's own
/// driver gets a toast. Presentation only (rule 5): nothing is decided or paid here.
/// </summary>
[DisallowMultipleComponent]
public sealed class TownExitPortal : MonoBehaviour
{
    [Tooltip("StreetGraph exit id (exit-west, exit-east, exit-north).")]
    public string exitId = "";
    public string displayName = "";
    [TextArea] public string toast = "Nothing out there but highway. The trash is back that way.";
    public float toastCooldown = 8f;

    float lastToast = -999f;

    static readonly List<TownExitPortal> all = new List<TownExitPortal>(3);
    public static IReadOnlyList<TownExitPortal> All => all;

    void OnEnable() => all.Add(this);
    void OnDisable() => all.Remove(this);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => all.Clear();

    void OnTriggerEnter(Collider other)
    {
        if (other == null) return;
        var truck = other.GetComponentInParent<TruckControllerV2>();
        if (truck == null || !(truck.IsLocalPlayerSeated || truck.IsLocalDriver)) return;   // the HUD of whoever drives it
        if (Time.unscaledTime - lastToast < toastCooldown) return;
        lastToast = Time.unscaledTime;
        GameHUD.Toast(toast, ToastStyle.Info, 3.5f);
    }
}
