using System.Collections;
using TMPro;
using UnityEngine;

/// <summary>
/// One Depot Row lot in <c>DepotRow.unity</c> (built at edit time by <c>Setup_DepotRow</c>, red-team T10: never spawned at
/// runtime; claiming only reveals and binds). Presents its <see cref="DepotLotState"/> on this machine:
/// <list type="bullet">
/// <item>unclaimed: a "FOR LEASE" sign on an empty pad; the shop, its pocket interior and its floor are fully off;</item>
/// <item>claimed: the plop (the shop drops in on a crane hook, dust, the sign bolts on; the host rolls the truck out),
/// the owner's sign ("Nick's" over "RUBBISH CO.") and colour;</item>
/// <item>owner away: shutters down and "BACK SOON";</item>
/// <item>the pocket interior is on for its owner, visitors, and the host while its owner is online (host-run chess/robots);
/// upgrades are visible to observers, while personal inventory displays remain owner-only.</item>
/// </list>
/// </summary>
[DisallowMultipleComponent]
public class DepotLot : MonoBehaviour
{
    public int lot;

    [Header("Street (DepotRow scene, on the Minivan Mile pad)")]
    public Transform facade;
    public Transform signRoot;
    public TextMeshPro ownerText;
    public TextMeshPro nameText;
    [Tooltip("Renderers painted in the business colour (sign board, door frame trims).")]
    public Renderer[] paintRenderers;
    public GameObject shutters;
    public GameObject backSoonSign;
    public GameObject forLeaseSign;
    public Transform craneHook;
    public GarageDoorController streetDoor;

    [Header("Pocket interior")]
    public GameObject interior;
    [Tooltip("Legacy personal roots. GarageUpgrades is shared and scoped by business; inventory displays remain owner-only.")]
    public GameObject[] ownerOnly;
    [Tooltip("An always-on floor under the bay while the lot is claimed, so a parked truck never falls through on a machine that has the interior off.")]
    public GameObject pocketFloor;
    public Transform interiorOnFoot;
    public Transform interiorTruck;
    public GarageDoorController interiorDoor;
    public LaptopController laptop;

    int seenPlop = int.MinValue;
    bool plopping;
    Vector3 facadeHome;
    bool homeKnown;
    int shownColor = -1;
    string shownOwner, shownName;
    MaterialPropertyBlock mpb;
    GameObject tableRestCollider;

    static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
    static readonly int ColorId = Shader.PropertyToID("_Color");

    public bool InteriorOn => interior != null && interior.activeSelf;
    /// <summary>True when the offscreen host-rest tabletop is supporting items in this claimed lot.</summary>
    public bool TableRestColliderOn => tableRestCollider != null && tableRestCollider.activeInHierarchy;

    void Awake()
    {
        if (facade != null) { facadeHome = facade.localPosition; homeKnown = true; }
        BuildTableRestCollider();
    }

    /// <summary>
    /// A guest simulates a freshly spilled item until it settles, then HandsNet makes the host simulate its rest.
    /// Other depots' interiors are normally off on the host, including their table colliders. Keep an exact,
    /// collider-only copy of the tabletop outside the switched interior so host-owned resting items cannot fall
    /// through the table before the host has ever visited that depot. The real colliders take over when we enter.
    /// </summary>
    void BuildTableRestCollider()
    {
        if (interior == null) return;
        var table = interior.GetComponentInChildren<SortingTable>(true);
        if (table == null) return;
        var source = table.transform.Find("Colliders");
        if (source == null)
        {
            Debug.LogWarning($"[DepotLot] Lot {lot + 1} has no sorting-table colliders to mirror.");
            return;
        }

        tableRestCollider = Instantiate(source.gameObject, transform, true);
        tableRestCollider.name = "SortingTableRestCollider";
        tableRestCollider.transform.SetPositionAndRotation(source.position, source.rotation);
        tableRestCollider.SetActive(false);

        // The owner's XL leaf lives in their owner-only upgrade rig. The host cannot rely on that rig being active
        // for a different business, so its offscreen rest collider covers the possible fold-out area as well.
        if (table.xlFrontExtension > 0f)
        {
            var leaf = new GameObject("XLLeafRestCollider");
            leaf.layer = gameObject.layer;
            leaf.transform.SetParent(tableRestCollider.transform, false);
            var box = leaf.AddComponent<BoxCollider>();
            box.center = new Vector3(table.topOffset.x,
                table.topHeight - 0.045f,
                table.topOffset.y - table.topSize.y * 0.5f - table.xlFrontExtension * 0.5f);
            box.size = new Vector3(table.topSize.x, 0.09f, table.xlFrontExtension + 0.02f);
        }
    }

    /// <summary>Called by <see cref="DepotRowScene"/> every frame with this lot's state.</summary>
    public void Present(bool active, in DepotLotState s, int localLot, int localInterior, int pendingInterior)
    {
        bool claimed = active && s.Claimed;
        bool online = claimed && s.Online;

        // Plop: a new claim seen while we're watching. A late joiner (first sight) just sees the shop standing.
        if (claimed && seenPlop != s.plopSerial)
        {
            bool firstSight = seenPlop == int.MinValue;
            seenPlop = s.plopSerial;
            if (!firstSight && isActiveAndEnabled) StartCoroutine(Plop());
            else if (firstSight && isActiveAndEnabled && Time.timeSinceLevelLoad < 20f && s.truckId == 0) StartCoroutine(Plop());
        }
        else if (!claimed) seenPlop = active ? s.plopSerial : int.MinValue;

        SetActive(forLeaseSign, !claimed);
        if (facade != null && !plopping) SetActive(facade.gameObject, claimed);
        SetActive(pocketFloor, claimed);
        SetActive(shutters, claimed && !online);
        SetActive(backSoonSign, claimed && !online);

        // The host must simulate each ONLINE business's chess and Magnus even while its player is outdoors.
        // Never turn on an unclaimed/offline business just because this machine is the host.
        bool simulate = online && NetBoot.Manager != null && NetBoot.Manager.IsServer;
        // A guest can leave a coworker's depot while a bag is still spraying. Keep its work surface alive
        // until the admitted contents are published; deactivating it would stop that peer's rip coroutine.
        bool finishingRip = false;
        if (claimed && interior != null && interior.activeSelf)
        {
            if (workingTable == null) workingTable = interior.GetComponentInChildren<SortingTable>(true);
            finishingRip = workingTable != null && workingTable.IsRipping;
            if (workingCompactor == null) workingCompactor = interior.GetComponentInChildren<Compactor>(true);
            finishingRip |= workingCompactor != null && workingCompactor.Busy;
        }
        bool inside = claimed && (simulate || finishingRip || lot == localLot || lot == localInterior || lot == pendingInterior);
        SetActive(interior, inside);
        SetActive(tableRestCollider, claimed && !inside);
        bool owner = claimed && lot == localLot;
        if (ownerOnly != null)
            foreach (var go in ownerOnly)
                SetActive(go, go != null && go.name == "GarageUpgrades" ? inside : owner);

        if (claimed) ApplySign(s);
    }

    SortingTable workingTable;
    Compactor workingCompactor;

    void ApplySign(in DepotLotState s)
    {
        string owner = s.ownerName.ToString();
        string name = s.businessName.ToString();
        if (owner != shownOwner && ownerText != null)
        {
            shownOwner = owner;
            ownerText.text = string.IsNullOrEmpty(owner) ? "" : Possessive(owner);
        }
        if (name != shownName && nameText != null)
        {
            bool first = shownName == null;
            shownName = name;
            nameText.text = string.IsNullOrEmpty(name) ? DepotBusinesses.DefaultName : name;
            if (!first && !plopping && signRoot != null) { Juice.Punch(signRoot, 0.12f, 0.3f); Sfx.Play("clang", signRoot.position, 0.5f); }
        }
        if (s.color != shownColor)
        {
            shownColor = s.color;
            Paint(paintRenderers, DepotColors.Get(s.color));
        }
    }

    public static string Possessive(string owner)
    {
        owner = owner.Trim();
        if (owner.Length > 18) owner = owner.Substring(0, 18);
        return owner.EndsWith("s") || owner.EndsWith("S") ? owner + "'" : owner + "'s";
    }

    void Paint(Renderer[] rs, Color c)
    {
        if (rs == null) return;
        if (mpb == null) mpb = new MaterialPropertyBlock();
        foreach (var r in rs)
        {
            if (r == null) continue;
            r.GetPropertyBlock(mpb);
            mpb.SetColor(BaseColorId, c);
            mpb.SetColor(ColorId, c);
            r.SetPropertyBlock(mpb);
        }
    }

    static void SetActive(GameObject go, bool on)
    {
        if (go != null && go.activeSelf != on) go.SetActive(on);
    }

    // ------------------------------------------------------------------ the plop

    IEnumerator Plop()
    {
        if (facade == null) yield break;
        if (!homeKnown) { facadeHome = facade.localPosition; homeKnown = true; }
        plopping = true;
        SetActive(facade.gameObject, true);
        Vector3 sky = facadeHome + Vector3.up * 26f;
        facade.localPosition = sky;
        if (signRoot != null) signRoot.localScale = Vector3.zero;
        if (craneHook != null) SetActive(craneHook.gameObject, true);
        Vector3 door = DepotRowLayout.DoorPoint(lot);
        Sfx.Play("whoosh", door + Vector3.up * 10f, 0.8f, 0.6f);

        // Lowered on the hook (eases in), then a short free drop.
        float t = 0f, dur = 1.35f;
        while (t < dur)
        {
            t += Time.deltaTime;
            float k = Mathf.Clamp01(t / dur);
            facade.localPosition = Vector3.LerpUnclamped(sky, facadeHome, k * k);
            yield return null;
        }
        facade.localPosition = facadeHome;

        // Touchdown.
        Sfx.Play("slam", door, 1f, 0.7f);
        Sfx.Play("thud", door, 1f, 0.6f);
        for (int i = -2; i <= 2; i++) Fx.Dust(door + new Vector3(i * 4.5f, 0.3f, 1.2f), 2.2f);
        Fx.Dust(door + new Vector3(-10f, 0.3f, -4f), 2f);
        Fx.Dust(door + new Vector3(10f, 0.3f, -4f), 2f);
        float near = Vector3.Distance(DepotRowWorld.LocalViewPoint, door);
        if (near < 45f) Juice.Shake(Mathf.Lerp(0.5f, 0.1f, near / 45f), 0.35f);
        Juice.Squash(facade, 0.08f, 0.35f);

        // The hook lets go and flies back up.
        if (craneHook != null)
        {
            Vector3 h0 = craneHook.localPosition;
            float u = 0f;
            while (u < 0.8f)
            {
                u += Time.deltaTime;
                craneHook.localPosition = h0 + Vector3.up * (u * u * 40f);
                yield return null;
            }
            craneHook.localPosition = h0;
            SetActive(craneHook.gameObject, false);
        }

        // The sign bolts on: clank, clank, ding.
        if (signRoot != null)
        {
            float s = 0f;
            while (s < 0.35f)
            {
                s += Time.deltaTime;
                float k = Mathf.Clamp01(s / 0.35f);
                float over = 1f + Mathf.Sin(k * Mathf.PI) * 0.25f;
                signRoot.localScale = Vector3.one * (k * over);
                yield return null;
            }
            signRoot.localScale = Vector3.one;
            Sfx.Play("clang", signRoot.position, 0.8f, 1.1f);
            yield return new WaitForSeconds(0.12f);
            Sfx.Play("clang", signRoot.position, 0.7f, 1.3f);
            yield return new WaitForSeconds(0.15f);
            Sfx.Play("ding", signRoot.position, 0.9f);
            Fx.Burst(signRoot.position, DepotColors.Get(shownColor < 0 ? lot : shownColor), 26, 5f);
        }
        plopping = false;
    }

    void OnDisable()
    {
        if (!plopping) return;
        plopping = false;
        if (facade != null && homeKnown) facade.localPosition = facadeHome;
        if (signRoot != null) signRoot.localScale = Vector3.one;
        if (craneHook != null) SetActive(craneHook.gameObject, false);
    }
}
