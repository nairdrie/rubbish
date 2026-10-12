using UnityEngine;

/// <summary>
/// E on a raccoon (F4): "Shoo the raccoon out of the hopper!" on a stowaway (from the street or riding the truck's back), or
/// "Bonk the raccoon off Dot!" on one latched onto a friend. An <see cref="InteractionZone"/> around the raccoon (tall enough
/// to reach from the ground and from the rear step), on the host's raccoon and on every guest's puppet; the host decides.
/// </summary>
public class CritterPoke : MonoBehaviour, IInteractable
{
    Raccoon r;
    float lastPoke = -9f;

    public static void Attach(Raccoon r)
    {
        if (r == null || r.GetComponentInChildren<CritterPoke>() != null) return;
        var go = new GameObject("PokeZone");
        go.layer = CritterWorld.CritterLayer;
        go.transform.SetParent(r.transform, false);
        var p = go.AddComponent<CritterPoke>();
        p.r = r;
        var z = go.AddComponent<InteractionZone>();
        z.center = new Vector3(0f, -0.9f, 0f);
        z.size = new Vector3(4.2f, 5.2f, 4.2f);
        z.mode = ZoneMode.OnFoot;
        z.score = 2.3f; // above the truck's "Hitch a ride" / "Hop off" (HitchStep.ZoneScore 2.05)
        z.targetOverride = p;
    }

    ulong Me => NightNet.Streaming ? NightNet.Instance.LocalId : 0;

    public bool CanInteract() => r != null && !r.Despawning && Time.time - lastPoke > 0.4f && r.PokePrompt(Me) != null;

    public string GetInteractionPrompt() => r != null ? r.PokePrompt(Me) ?? "" : "";

    public void OnInteract(GameObject interactor)
    {
        if (!CanInteract()) return;
        lastPoke = Time.time;
        Vector3 at = r.transform.position + Vector3.up * 0.6f;
        Sfx.Play("thud", at, 0.8f, 1.3f);
        Fx.Burst(at, new Color(1f, 0.9f, 0.35f), 8, 2.5f);
        Juice.Shake(0.08f, 0.1f);
        NightNet.RequestPoke(r);
    }

    public void Highlight(bool highlight) { }
}
