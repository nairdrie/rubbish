using UnityEngine;

/// <summary>
/// The status signs on the gates of districts that open by contract (Campaign.cs) say what actually opens them. The gate
/// script (<see cref="DistrictGate"/>) is part of the baked town and still prints "PERMIT ONLY: Rank 4 + $600 permit"; editing it
/// would make the Editor ask for a new bake, so this runtime layer rewrites that one sign while the gate is closed.
/// Fold it into <c>DistrictGate.UpdateSign</c> the next time the town is baked anyway.
/// </summary>
public sealed class ContractGateSigns : MonoBehaviour
{
    const float EverySeconds = 0.5f;
    static ContractGateSigns instance;
    DistrictGate[] gates = new DistrictGate[0];
    float nextLook, nextFind;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => instance = null;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (instance != null || !Application.isPlaying || Application.isBatchMode) return;
        var go = new GameObject("[ContractGateSigns]");
        DontDestroyOnLoad(go);
        instance = go.AddComponent<ContractGateSigns>();
    }

    void Update()
    {
        float now = Time.unscaledTime;
        if (now < nextLook) return;
        nextLook = now + EverySeconds;
        if (now >= nextFind)
        {
            nextFind = now + 5f;     // gates come and go with the town scene
            gates = FindObjectsByType<DistrictGate>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        }
        for (int i = 0; i < gates.Length; i++)
        {
            var g = gates[i];
            if (g == null || g.statusText == null || g.IsOpen) continue;
            string why = Campaign.LockedReason(g.districtId);
            if (why == null) continue;
            string text = "<color=#FF6A5A>CONTRACT ONLY</color>\n<size=45%>" + why + "</size>";
            if (g.statusText.text != text) g.statusText.text = text;
        }
    }
}
