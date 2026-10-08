using System.Collections;
using TMPro;
using UnityEngine;

/// <summary>
/// A district's gate on its access road (Docs/Progression.md §5.3), placed by NeighborhoodBuilder. Vehicles only: a
/// barrier collider (layer Walls) spans the road while the company lacks the district's permit; a turnstile gap beside
/// it lets people walk in and gawk (nothing spawns in a locked district, so there's no trash to steal).
///   - [E] in the InteractionZone (Either, in front of the barrier): before the rank, the locked prompt
///     "Minivan Meadows: needs Rank 4 (Certified Bin Technician) + Collection Permit ($600)"; once the rank is met,
///     "Buy Minivan Meadows permit ($600)" → ScrapazonStore.TryBuy(permitId).
///   - Opens the moment GameSession.GetUpgradeLevel(permitId) > 0 (instantly on scene load, animated when bought).
///   - The first drive-through after unlocking snaps a giant red ribbon: confetti, the DistrictWelcome card, and
///     ProgressionEvents.DistrictEntered. Later entries (driving in) raise DistrictEntered again (once a minute at most).
/// Root transform: +Z points INTO the district. The root's trigger BoxCollider is the ribbon line (district side).
/// Co-op (Multiplayer.md §4.8): the permit is company state; the barrier is local to each machine.
/// </summary>
public class DistrictGate : MonoBehaviour, IInteractable
{
    public string districtId = Districts.MinivanMeadows;
    [Tooltip("Additional entrance using the same permit; the primary gate owns the permit-purchase celebration.")]
    public bool secondaryEntrance;

    [Header("Built by NeighborhoodBuilder")]
    [Tooltip("Blocks vehicles while locked (layer Walls).")]
    public Collider barrierCollider;
    [Tooltip("Moving barrier part (boom arm / gate leaf); rotates from closedEuler to openEuler (local).")]
    public Transform barrierPivot;
    public Vector3 closedEuler, openEuler;
    [Tooltip("Optional second leaf (double gates).")]
    public Transform barrierPivot2;
    public Vector3 closedEuler2, openEuler2;
    [Tooltip("The giant red ribbon (shown once unlocked, until the first drive-through snaps it).")]
    public GameObject ribbon;
    [Tooltip("Status sign text: CLOSED / OPEN.")]
    public TextMeshPro statusText;

    public DistrictDef District => Districts.Get(districtId);
    public bool Unlocked => DistrictAccess.LocalCanEnter(District);
    /// <summary>The barrier is out of the way (collider off).</summary>
    public bool IsOpen { get; private set; }
    public string RibbonKey => "ribbon_cut_" + districtId;
    public bool RibbonCut => GameSession.GetCounter(RibbonKey) > 0;

    float openT;              // 0 closed .. 1 open (animated)
    float lastEnterTime = -999f, lastDenyTime = -999f;
    Coroutine anim;

    void Start()
    {
        GameSession.UpgradeChanged += OnUpgradeChanged;
        ProgressionEvents.DistrictUnlocked += OnDistrictUnlocked;
        BusinessUpgradeNet.Changed += OnCrewAccessChanged;
        Payroll.Changed += OnCrewAccessChanged;
        Businesses.Changed += OnCrewAccessChanged;
        SyncState(true);
    }

    void OnDestroy()
    {
        GameSession.UpgradeChanged -= OnUpgradeChanged;
        ProgressionEvents.DistrictUnlocked -= OnDistrictUnlocked;
        BusinessUpgradeNet.Changed -= OnCrewAccessChanged;
        Payroll.Changed -= OnCrewAccessChanged;
        Businesses.Changed -= OnCrewAccessChanged;
    }

    void OnCrewAccessChanged() { if (Unlocked != IsOpen) SyncState(false); }

    void OnUpgradeChanged(string id, int level)
    {
        if (id == District.permitId) SyncState(false);
    }

    void OnDistrictUnlocked(string id)
    {
        if (id == districtId) SyncState(false);
    }

    /// <summary>Apply the permit state: open/close the barrier (instantly or animated), show/hide the ribbon and signs.</summary>
    public void SyncState(bool instant)
    {
        bool open = Unlocked;
        bool wasOpen = IsOpen;
        IsOpen = open;
        if (barrierCollider != null) barrierCollider.enabled = !open;
        if (ribbon != null) ribbon.SetActive(open && !RibbonCut);
        UpdateSign();
        if (instant || !Application.isPlaying)
        {
            openT = open ? 1f : 0f;
            ApplyPose();
        }
        else if (open != wasOpen)
        {
            if (anim != null) StopCoroutine(anim);
            anim = StartCoroutine(AnimateOpen(open));
        }
    }

    void UpdateSign()
    {
        if (statusText == null) return;
        var d = District;
        statusText.text = IsOpen
            ? "<color=#7CFF8A>OPEN</color>\n<size=45%>" + (Districts.IsUnlocked(d) ? "your business permit" : "your employer's permit · while hired") + "</size>"
            : $"<color=#FF6A5A>PERMIT ONLY</color>\n<size=45%>Rank {d.rankRequired} + {Economy.Money(d.permitPrice)} permit</size>";
    }

    void ApplyPose()
    {
        float e = openT * openT * (3f - 2f * openT);
        if (barrierPivot != null) barrierPivot.localRotation = Quaternion.Slerp(Quaternion.Euler(closedEuler), Quaternion.Euler(openEuler), e);
        if (barrierPivot2 != null) barrierPivot2.localRotation = Quaternion.Slerp(Quaternion.Euler(closedEuler2), Quaternion.Euler(openEuler2), e);
    }

    IEnumerator AnimateOpen(bool open)
    {
        float target = open ? 1f : 0f;
        if (open && !secondaryEntrance && Districts.IsUnlocked(District))
        {
            var d = District;
            Vector3 p = transform.position + Vector3.up * 3f;
            Fx.Burst(p, new Color(1f, 0.85f, 0.2f), 30, 6f);
            Fx.Burst(p, new Color(0.4f, 1f, 0.5f), 20, 5f);
            Sfx.Play("door", p, 1f, 0.8f);
            Sfx.Play("legendary", p, 0.8f, 1.15f);
            GameHUD.Toast($"PERMIT APPROVED! {d.name} is open for business. Mind the ribbon.", ToastStyle.Legendary, 4f);
            Juice.Shake(0.2f, 0.25f);
        }
        // A derpy overshoot: swings a bit past, wobbles back.
        float t = 0f;
        float start = openT;
        while (t < 1.4f)
        {
            t += Time.deltaTime;
            float k = Mathf.Clamp01(t / 1.1f);
            float wobble = open ? Mathf.Sin(k * Mathf.PI * 2.5f) * (1f - k) * 0.12f : 0f;
            openT = Mathf.Lerp(start, target, 1f - Mathf.Pow(1f - k, 3f)) + wobble;
            ApplyPose();
            yield return null;
        }
        openT = target;
        ApplyPose();
        if (barrierPivot != null) Juice.Punch(barrierPivot, 0.15f, 0.3f);
        anim = null;
    }

    // ------------------------------------------------------------------ IInteractable (locked gate only)

    public bool CanInteract() => !Unlocked;

    public string GetInteractionPrompt()
    {
        var d = District;
        var permit = ScrapazonCatalog.Get(d.permitId);
        if (ScrapazonStore.IsLocked(permit, out string why)) return d.name + ": " + why;
        if (Progression.HasRank(d.rankRequired)) return $"Buy {d.name} permit ({Economy.Money(d.permitPrice)})";
        return $"{d.name}: needs Rank {d.rankRequired} ({Progression.RankTitle(d.rankRequired)}) + Collection Permit ({Economy.Money(d.permitPrice)})";
    }

    public void OnInteract(GameObject interactor)
    {
        if (Unlocked) return;
        var d = District;
        Vector3 at = transform.position + Vector3.up * 3.2f;
        if (!Progression.HasRank(d.rankRequired))
        {
            Deny(at, $"The gate guy checks his clipboard. \"Rank {d.rankRequired}. {Progression.RankTitle(d.rankRequired)}. You are not that.\"");
            return;
        }
        var result = ScrapazonStore.TryBuy(d.permitId, out string message);
        if (result == ScrapazonPurchase.Ok)
        {
            Sfx.Play("cash", at, 1f, 1f);
            GameHUD.FloatingText(at, $"-{Economy.Money(d.permitPrice)} PERMIT!", new Color(1f, 0.85f, 0.3f), 1.4f);
            SyncState(false);
            return;
        }
        Deny(at, string.IsNullOrEmpty(message) ? $"Couldn't buy the {d.name} permit ({result})." : message);
    }

    void Deny(Vector3 at, string message)
    {
        if (Time.unscaledTime - lastDenyTime < 0.4f) return;
        lastDenyTime = Time.unscaledTime;
        Sfx.Play("buzz", at, 0.9f, 0.9f);
        GameHUD.FloatingText(at, "NOPE", new Color(1f, 0.35f, 0.3f), 1.2f);
        GameHUD.Toast(message, ToastStyle.Bad, 3.5f);
        if (barrierPivot != null) Juice.Punch(barrierPivot, 0.12f, 0.25f);
        if (statusText != null) Juice.Punch(statusText.transform, 0.3f, 0.3f);
    }

    public void Highlight(bool highlight) { }

    // ------------------------------------------------------------------ the ribbon line (root trigger)

    void OnTriggerEnter(Collider other)
    {
        if (!IsOpen || other == null) return;
        var truck = other.GetComponentInParent<TruckControllerV2>();
        if (truck == null) return;                                   // vehicles only: walking through doesn't count
        var body = other.attachedRigidbody;
        Vector3 v = body != null ? body.linearVelocity : truck.transform.forward;
        bool entering = Vector3.Dot(v, transform.forward) > 0.1f || body == null;

        if (!RibbonCut)
        {
            SnapRibbon(truck.transform.position);
            return;
        }
        if (entering && Time.unscaledTime - lastEnterTime > 60f)
        {
            lastEnterTime = Time.unscaledTime;
            ProgressionEvents.RaiseDistrictEntered(districtId);
        }
    }

    /// <summary>The first drive-through: snap the ribbon, confetti, welcome card, DistrictEntered.</summary>
    public void SnapRibbon(Vector3 at)
    {
        if (RibbonCut) return;
        GameSession.SetCounter(RibbonKey, 1);
        // One district permit, one ceremonial ribbon, even when the district has several entrances.
        foreach (var other in FindObjectsByType<DistrictGate>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (other != this && other.districtId == districtId && other.ribbon != null) other.ribbon.SetActive(false);
        lastEnterTime = Time.unscaledTime;
        var d = District;
        Vector3 p = ribbon != null ? ribbon.transform.position : at + Vector3.up * 2f;
        Color[] confetti = { new Color(1f, 0.25f, 0.3f), new Color(1f, 0.85f, 0.2f), new Color(0.35f, 0.8f, 1f), new Color(0.5f, 1f, 0.45f) };
        foreach (var c in confetti) Fx.Burst(p, c, 36, 9f);
        Sfx.Play("pop", p, 1f, 0.7f);
        Sfx.Play("whoosh", p, 0.9f, 1f);
        Juice.Shake(0.3f, 0.3f);
        GameHUD.FloatingText(p + Vector3.up, "SNIP!", new Color(1f, 0.3f, 0.35f), 1.6f);
        if (ribbon != null) StartCoroutine(FlingRibbon(ribbon.transform));
        DistrictWelcome.Show(d);
        ProgressionEvents.RaiseDistrictEntered(districtId);
    }

    IEnumerator FlingRibbon(Transform root)
    {
        // The two halves flop outward and shrink away; the bow pops off upward.
        var parts = new Transform[root.childCount];
        for (int i = 0; i < parts.Length; i++) parts[i] = root.GetChild(i);
        var startPos = new Vector3[parts.Length];
        var startRot = new Quaternion[parts.Length];
        var startScale = new Vector3[parts.Length];
        for (int i = 0; i < parts.Length; i++) { startPos[i] = parts[i].localPosition; startRot[i] = parts[i].localRotation; startScale[i] = parts[i].localScale; }
        float t = 0f;
        while (t < 0.9f)
        {
            t += Time.deltaTime;
            float k = t / 0.9f;
            for (int i = 0; i < parts.Length; i++)
            {
                var pt = parts[i];
                if (pt == null) continue;
                float side = startPos[i].x < -0.1f ? -1f : startPos[i].x > 0.1f ? 1f : 0f;
                pt.localPosition = startPos[i] + new Vector3(side * k * 3f, (side == 0f ? 6f : 1.5f) * k - 5f * k * k * (side == 0f ? 0.3f : 1f), 0f);
                pt.localRotation = startRot[i] * Quaternion.Euler(side * 70f * k, side * 40f * k, side * -90f * k + (side == 0f ? 360f * k : 0f));
                pt.localScale = startScale[i] * Mathf.Lerp(1f, 0.05f, k * k);
            }
            yield return null;
        }
        root.gameObject.SetActive(false);
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i] == null) continue;
            parts[i].localPosition = startPos[i]; parts[i].localRotation = startRot[i]; parts[i].localScale = startScale[i];
        }
    }

    void OnDrawGizmos()
    {
        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.color = new Color(1f, 0.3f, 0.3f, 0.6f);
        Gizmos.DrawLine(Vector3.zero, Vector3.forward * 4f);
        Gizmos.DrawWireCube(Vector3.up * 2f, new Vector3(10f, 4f, 0.3f));
    }
}
