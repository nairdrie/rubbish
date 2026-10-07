using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Local-only label for a Depot Row business truck. Its identity comes from the lot's replicated
/// business state, never from the NetworkObject's current driving/transform owner.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(1100)]
public sealed class DepotTruckNameTag : MonoBehaviour
{
    // The truck roof is ~3.32 m above its root. Keep the whole plate above the mesh.
    const float LabelHeight = 4f;
    const float MaxDistance = 34f;

    NetworkObject truck;
    Canvas canvas;
    CanvasGroup group;
    RectTransform plate;
    TextMeshProUGUI businessText;
    TextMeshProUGUI ownerText;
    Image marker;
    int lot = -1;
    string shownBusiness, shownOwner;
    int shownColor = -1;
    float nextRefresh, nextSight;
    bool clearSight;
    readonly RaycastHit[] sightHits = new RaycastHit[16];

    public void Apply(int businessLot) => lot = businessLot;

    void Awake()
    {
        truck = GetComponent<NetworkObject>();
        if (!Application.isBatchMode) Build();
    }

    void Build()
    {
        var go = new GameObject("Business truck name tag", typeof(RectTransform));
        go.layer = 5;
        go.transform.SetParent(transform, false);
        var rect = (RectTransform)go.transform;
        rect.sizeDelta = new Vector2(300f, 72f);
        canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.enabled = false;
        group = go.AddComponent<CanvasGroup>();
        group.alpha = 0f;
        group.blocksRaycasts = false;
        group.interactable = false;

        plate = FeelUi.Rect("Plate", rect, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
            Vector2.zero, new Vector2(300f, 72f));
        FeelUi.Img(plate, "Backing", FeelUi.Rounded, new Color(0.08f, 0.08f, 0.13f, 0.89f), 0.38f);
        var markRect = FeelUi.Rect("Business colour", plate, new Vector2(0f, 0.5f), new Vector2(0.5f, 0.5f),
            new Vector2(11f, 0f), new Vector2(9f, 52f));
        marker = markRect.gameObject.AddComponent<Image>();
        marker.sprite = FeelUi.Rounded;
        marker.raycastTarget = false;

        businessText = FeelUi.Text(plate, "Business", 28f, Color.white, TextAlignmentOptions.Center);
        businessText.richText = false;
        businessText.textWrappingMode = TextWrappingModes.NoWrap;
        businessText.overflowMode = TextOverflowModes.Ellipsis;
        businessText.rectTransform.offsetMin = new Vector2(28f, 29f);
        businessText.rectTransform.offsetMax = new Vector2(-10f, -6f);

        ownerText = FeelUi.Text(plate, "Owner", 18f, new Color(0.86f, 0.88f, 0.96f), TextAlignmentOptions.Center);
        ownerText.richText = false;
        ownerText.textWrappingMode = TextWrappingModes.NoWrap;
        ownerText.overflowMode = TextOverflowModes.Ellipsis;
        ownerText.rectTransform.offsetMin = new Vector2(28f, 5f);
        ownerText.rectTransform.offsetMax = new Vector2(-10f, -43f);
    }

    void OnDestroy()
    {
        if (canvas != null) Destroy(canvas.gameObject);
    }

    void LateUpdate()
    {
        if (canvas == null) return;
        var net = DepotRowNet.Instance;
        var cam = Camera.main;
        DepotLotState state = default;
        bool valid = DepotRowWorld.Active && !DepotRowWorld.Transitioning &&
                     truck != null && truck.IsSpawned && lot >= 0 && cam != null &&
                     net != null && net.TryGetLot(lot, out state) && state.Claimed &&
                     state.truckId == truck.NetworkObjectId;
        if (!valid) { Hide(); return; }

        // A pocket garage is physically far below the street. Never show its truck's label to a
        // player outside that same pocket, even if a camera happens to see through unloaded scenery.
        if (DepotRowLayout.InteriorAt(transform.position) != DepotRowWorld.LocalInterior)
        {
            Hide();
            return;
        }

        Vector3 at = transform.position + Vector3.up * LabelHeight;
        Vector3 sight = at - cam.transform.position;
        float distance = sight.magnitude;
        if (distance < 2.5f || distance > MaxDistance || Vector3.Dot(cam.transform.forward, sight) <= 0f)
        {
            Hide();
            return;
        }

        if (Time.unscaledTime >= nextSight)
        {
            nextSight = Time.unscaledTime + 0.15f;
            int count = Physics.RaycastNonAlloc(cam.transform.position, sight / distance,
                sightHits, distance - 0.12f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            clearSight = count < sightHits.Length;
            for (int i = 0; i < count; i++)
                if (!sightHits[i].collider.transform.IsChildOf(transform)) { clearSight = false; break; }
        }
        if (Time.unscaledTime >= nextRefresh)
        {
            nextRefresh = Time.unscaledTime + 0.25f;
            Refresh(state);
        }

        canvas.enabled = true;
        var rect = (RectTransform)canvas.transform;
        rect.position = at;
        rect.rotation = cam.transform.rotation;
        float parentScale = Mathf.Max(0.01f, Mathf.Abs(transform.lossyScale.x));
        rect.localScale = Vector3.one * (Mathf.Clamp(distance * 0.00052f, 0.0035f, 0.017f) / parentScale);
        float fade = Mathf.Clamp01((MaxDistance - distance) / 5f);
        group.alpha = Mathf.MoveTowards(group.alpha, clearSight ? fade : 0f, Time.unscaledDeltaTime * 9f);
    }

    void Refresh(in DepotLotState state)
    {
        string business = state.businessName.ToString();
        if (string.IsNullOrWhiteSpace(business)) business = DepotBusinesses.DefaultName;
        string owner = state.ownerName.ToString();
        if (string.IsNullOrWhiteSpace(owner)) owner = "Neighbour";
        if (business != shownBusiness || owner != shownOwner)
        {
            shownBusiness = business;
            shownOwner = owner;
            businessText.text = business;
            ownerText.text = "Owned by " + owner;
            float width = Mathf.Clamp(Mathf.Max(businessText.GetPreferredValues(business).x,
                ownerText.GetPreferredValues(ownerText.text).x) + 52f, 220f, 355f);
            plate.sizeDelta = new Vector2(width, plate.sizeDelta.y);
        }
        if (state.color != shownColor)
        {
            shownColor = state.color;
            marker.color = DepotColors.Get(shownColor);
        }
    }

    void Hide()
    {
        canvas.enabled = false;
        group.alpha = 0f;
    }
}
