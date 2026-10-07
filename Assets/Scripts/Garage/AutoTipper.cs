using UnityEngine;

/// <summary>
/// Auto-Tipper (auto_tipper L1, Docs/Progression.md §6.2): "Lever Pulls Itself, Creepily". Once the TIP IT lever is
/// ready (truck in the bay with cargo) and the truck has sat under 0.2 m/s for 1.5 s, the lever pulls itself. The faster
/// unload (0.03 s per bag) and L2's Direct Feed live in TruckUnloader. Host decides (GarageRig.Decides).
/// The visual is a little piston arm next to the lever (built by Setup_GarageUpgrades, shown by level) that this pumps.
/// </summary>
public class AutoTipper : MonoBehaviour
{
    public GarageRig rig;
    [Tooltip("Optional: the piston that 'pulls' the lever (punched when it fires).")]
    public Transform piston;
    [Tooltip("Optional: a little status lamp (green = armed, amber = waiting for the truck to stop).")]
    public Renderer lamp;
    public float stillSpeed = 0.2f;
    public float stillSeconds = 1.5f;

    float stillSince = -1f;
    float nextLamp;
    MaterialPropertyBlock mpb;

    TruckUnloader Unloader => rig != null ? rig.unloader : null;

    /// <summary>Seconds the parked truck has been still (0 when there's no parked truck).</summary>
    public float StillFor => stillSince < 0f ? 0f : Time.time - stillSince;

    void Awake()
    {
        if (rig == null) rig = GetComponentInParent<GarageRig>();
    }

    void Update()
    {
        var un = Unloader;
        bool on = DepotMachineContext.Level(this, ShopIds.AutoTipper) >= 1 && un != null;
        if (!on) { stillSince = -1f; SetLamp(Color.black); return; }

        bool ready = un.IsReady;
        bool still = ready && un.ParkedTruckSpeed < stillSpeed;
        if (!still) { stillSince = -1f; SetLamp(ready ? new Color(1f, 0.6f, 0.1f) : new Color(0.15f, 0.25f, 0.15f)); return; }
        if (stillSince < 0f) stillSince = Time.time;
        SetLamp(Color.green);
        if (!GarageRig.Decides || Time.time - stillSince < stillSeconds) return;

        stillSince = -1f;
        if (un.AutoPull())
        {
            if (piston != null) Juice.Punch(piston, 0.35f, 0.3f);
            Sfx.Play("whoosh", un.transform.position + Vector3.up, 0.6f, 0.7f);
            Fx.Burst(un.transform.position + Vector3.up * 1.4f, new Color(0.7f, 0.85f, 1f), 10, 2f);
        }
    }

    void SetLamp(Color c)
    {
        if (lamp == null || Time.time < nextLamp) return;
        nextLamp = Time.time + 0.2f;
        mpb ??= new MaterialPropertyBlock();
        lamp.GetPropertyBlock(mpb);
        mpb.SetColor("_BaseColor", c);
        mpb.SetColor("_EmissionColor", c * 2f);
        lamp.SetPropertyBlock(mpb);
    }
}
