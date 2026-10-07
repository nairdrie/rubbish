using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

/// <summary>A local-only, world-space label for other players. Names come from the replicated roster.</summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(1100)]
public sealed class PlayerNameTag : MonoBehaviour
{
    const float HeadHeight = 1.6f;
    const float MaxDistance = 28f;

    NetworkObject player;
    ChuckPuppet puppet;
    Canvas canvas;
    CanvasGroup group;
    RectTransform plate;
    TextMeshProUGUI nameText;
    Image marker;
    string shownName;
    Color shownColor;
    float nextRefresh, nextSight;
    bool clearSight;

    void Awake()
    {
        player = GetComponent<NetworkObject>();
        puppet = GetComponent<ChuckPuppet>();
        if (!Application.isBatchMode) Build();
    }

    void Build()
    {
        var go = new GameObject("Player name tag", typeof(RectTransform));
        go.layer = 5;
        go.transform.SetParent(transform, false);
        var rect = (RectTransform)go.transform;
        rect.localPosition = Vector3.up * HeadHeight;
        rect.sizeDelta = new Vector2(200f, 43f);
        canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.enabled = false;
        group = go.AddComponent<CanvasGroup>();
        group.alpha = 0f;
        group.blocksRaycasts = false;
        group.interactable = false;

        plate = FeelUi.Rect("Plate", rect, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
            Vector2.zero, new Vector2(200f, 43f));
        FeelUi.Img(plate, "Backing", FeelUi.Rounded, new Color(0.09f, 0.08f, 0.15f, 0.88f), 0.38f);
        var markRect = FeelUi.Rect("Player colour", plate, new Vector2(0f, 0.5f), new Vector2(0.5f, 0.5f),
            new Vector2(10f, 0f), new Vector2(8f, 26f));
        marker = markRect.gameObject.AddComponent<Image>();
        marker.sprite = FeelUi.Rounded;
        marker.raycastTarget = false;
        nameText = FeelUi.Text(plate, "Name", 30f, Color.white, TextAlignmentOptions.Center);
        nameText.richText = false; // a Steam name is plain text, even if it contains markup characters
        nameText.overflowMode = TextOverflowModes.Ellipsis;
        nameText.rectTransform.offsetMin = new Vector2(20f, 0f);
        nameText.rectTransform.offsetMax = new Vector2(-10f, 0f);
    }

    void OnDestroy()
    {
        // Normally destroyed with the player root; also handle component removal in the Editor.
        if (canvas != null) Destroy(canvas.gameObject);
    }

    void LateUpdate()
    {
        if (canvas == null) return;
        var roster = NetRoster.Instance;
        var cam = Camera.main;
        bool valid = player != null && player.IsSpawned && !player.IsOwner &&
                     roster != null && roster.TryGetByClient(player.OwnerClientId, out _) &&
                     NetSession.InSession && !NetSession.IsSolo && cam != null;
        if (!valid) { canvas.enabled = false; group.alpha = 0f; return; }

        Vector3 at = puppet != null && puppet.head != null
            ? puppet.head.position + Vector3.up * 0.54f
            : transform.position + Vector3.up * HeadHeight;
        TruckControllerV2 driverTruck = null;
        if (TruckCabVisuals.TryDriverHead(player.OwnerClientId, out _, out driverTruck))
            at = ShopModKit.TruckPoint(driverTruck.transform, TruckCabVisuals.DriverNameMetres);
        Vector3 sight = at - cam.transform.position;
        float distance = sight.magnitude;
        bool inFront = Vector3.Dot(cam.transform.forward, sight) > 0f;
        if (distance > MaxDistance || !inFront) { canvas.enabled = false; group.alpha = 0f; return; }
        if (Time.unscaledTime >= nextSight)
        {
            nextSight = Time.unscaledTime + 0.15f;
            clearSight = !Physics.Raycast(cam.transform.position, sight / Mathf.Max(0.01f, distance),
                out var hit, distance - 0.12f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore) ||
                hit.collider.transform.IsChildOf(transform) || (driverTruck != null && hit.collider.transform.IsChildOf(driverTruck.transform));
        }
        if (Time.unscaledTime >= nextRefresh)
        {
            nextRefresh = Time.unscaledTime + 0.25f;
            Refresh(roster);
        }

        canvas.enabled = true;
        var rect = (RectTransform)canvas.transform;
        rect.position = at;
        rect.rotation = cam.transform.rotation;
        rect.localScale = Vector3.one * Mathf.Clamp(distance * 0.00065f, 0.0028f, 0.016f);
        float fade = Mathf.Clamp01((MaxDistance - distance) / 5f);
        group.alpha = Mathf.MoveTowards(group.alpha, clearSight ? fade : 0f, Time.unscaledDeltaTime * 9f);
    }

    void Refresh(NetRoster roster)
    {
        if (!roster.TryGetByClient(player.OwnerClientId, out var entry)) return;
        string next = entry.displayName.ToString();
        if (string.IsNullOrWhiteSpace(next)) next = "Player " + (player.OwnerClientId + 1);
        if (next != shownName)
        {
            shownName = next;
            nameText.text = next;
            float width = Mathf.Clamp(nameText.GetPreferredValues(next).x + 44f, 105f, 250f);
            plate.sizeDelta = new Vector2(width, plate.sizeDelta.y);
        }
        Color nextColor = MapBeacon.PlayerColor(GetComponent<PlayerController>());
        if (nextColor != shownColor) { shownColor = nextColor; marker.color = nextColor; }
    }
}
