using System.Collections.Generic;
using TMPro;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Root of <c>DepotRow.unity</c> (built by <c>Setup_DepotRow</c>). Every frame it presents <see cref="DepotRowNet"/>'s lots
/// through the four <see cref="DepotLot"/>s, and does this machine's local bits of the one world:
/// <list type="bullet">
/// <item>which pocket interiors are on (yours, and the one you're standing in: unclaimed lots are fully off);</item>
/// <item>indoor lighting while the local camera is inside a shop (the garage's flat ambient, no fog, no sun), restored
/// on the way out;</item>
/// <item>business colours on trucks (body paint) and players (Chuck's cap, shirt and sleeves);</item>
/// <item>a pre-coastal town's old RUBBISH CO. building (if one is still in the scene) is closed in the one world.</item>
/// </list>
/// <para><b>Legacy scene flow (solo):</b> the host loads this scene next to the town too
/// (<see cref="DepotRowNet"/>), but only its street side is used: lot 1 is the player's shop with their name and colour
/// on the sign, its roller door leads to ShopScene, the other lots are shuttered FOR LEASE units
/// (<see cref="DepotFlowRules.LookFor"/>). No pocket interior, indoor lighting, truck or player tint runs.</para>
/// </summary>
[DefaultExecutionOrder(900)] // after DayNightSky's Update so indoor lighting wins this frame
public class DepotRowScene : MonoBehaviour
{
    public static DepotRowScene Instance { get; private set; }

    public DepotLot[] lots = new DepotLot[DepotRowLayout.LotCount];

    [Header("Indoor lighting (copied from ShopScene's RenderSettings)")]
    public AmbientMode shopAmbientMode = AmbientMode.Flat;
    public Color shopAmbientSky = new Color(0.212f, 0.227f, 0.259f);
    public Color shopAmbientEquator = new Color(0.114f, 0.125f, 0.133f);
    public Color shopAmbientGround = new Color(0.047f, 0.043f, 0.035f);
    public float shopReflectionIntensity = 0f;

    public static DepotLot Lot(int lot) =>
        Instance != null && Instance.lots != null && lot >= 0 && lot < Instance.lots.Length ? Instance.lots[lot] : null;

    // indoor lighting snapshot
    bool indoorLit;
    AmbientMode savedMode;
    Color savedSky, savedEq, savedGround;
    bool savedFog;
    float savedRefl;
    Light savedSun;
    bool savedSunEnabled;

    float nextTint, nextLegacySign;
    string legacyOwner = "", legacyName = DepotBusinesses.DefaultName;
    int legacyColor;
    bool townYardDone;
    readonly List<PlayerController> players = new List<PlayerController>(8);
    static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");

    void OnEnable() => Instance = this;

    void OnDisable()
    {
        if (Instance == this) Instance = null;
        EndIndoorLighting();
    }

    void Update()
    {
        var net = DepotRowNet.Instance;
        bool active = DepotRowWorld.Active;
        int localLot = active ? DepotRowWorld.LocalLot : -1;
        int here = active ? DepotRowWorld.LocalInterior : -1;
        int pending = active ? DepotRowWorld.PendingInterior : -1;
        bool legacy = !active && DepotRowWorld.LegacyStreet;
        if (legacy && Time.unscaledTime >= nextLegacySign) RefreshLegacySign();
        for (int i = 0; i < lots.Length; i++)
        {
            var l = lots[i];
            if (l == null) continue;
            if (legacy)
            {
                l.PresentLegacy(DepotFlowRules.LookFor(DepotFlow.LegacyScenes, i, false, false), legacyOwner, legacyName, legacyColor);
                continue;
            }
            DepotLotState s = default;
            bool have = active && net.TryGetLot(i, out s);
            l.Present(have, s, localLot, here, pending);
        }

        if (active && !townYardDone) { townYardDone = true; MakeTownYard(); }

        if (active && Time.unscaledTime >= nextTint)
        {
            nextTint = Time.unscaledTime + 0.5f;
            TintTrucks(net);
            TintPlayers(net);
        }
    }

    void LateUpdate()
    {
        bool inside = DepotRowWorld.Active && (DepotRowWorld.LocalInterior >= 0 || DepotRowWorld.PendingInterior >= 0);
        if (inside) ApplyIndoorLighting();
        else EndIndoorLighting();
    }

    // ------------------------------------------------------------------ the legacy shop's sign

    /// <summary>
    /// Whose shop lot 1 is in the legacy flow: the host's business (solo: yours). Same sources as a claimed lot's sign
    /// (<see cref="DepotBusinesses"/>), so a rename on the garage laptop shows on the street the next time you step out.
    /// </summary>
    void RefreshLegacySign()
    {
        nextLegacySign = Time.unscaledTime + 0.5f;
        ulong host = NetworkManager.ServerClientId;
        legacyOwner = DepotBusinesses.OwnerNameFor(host);
        string name = DepotBusinesses.NameFor(host);
        var nm = NetBoot.Manager;
        if (name == null && nm != null && nm.IsServer) name = DepotBusinesses.Clean(GameSession.BusinessName);
        legacyName = name ?? DepotBusinesses.DefaultName;
        legacyColor = DepotBusinesses.ColorIndexFor(host, DepotFlowRules.LegacyShopLot);
    }

    // ------------------------------------------------------------------ lighting

    void ApplyIndoorLighting()
    {
        if (!indoorLit)
        {
            indoorLit = true;
            savedMode = RenderSettings.ambientMode;
            savedSky = RenderSettings.ambientSkyColor;
            savedEq = RenderSettings.ambientEquatorColor;
            savedGround = RenderSettings.ambientGroundColor;
            savedFog = RenderSettings.fog;
            savedRefl = RenderSettings.reflectionIntensity;
            savedSun = RenderSettings.sun;
            savedSunEnabled = savedSun != null && savedSun.enabled;
        }
        // DayNightSky writes these every Update; we run later in the frame, so the garage look wins while inside.
        RenderSettings.ambientMode = shopAmbientMode;
        RenderSettings.ambientSkyColor = shopAmbientSky;
        RenderSettings.ambientEquatorColor = shopAmbientEquator;
        RenderSettings.ambientGroundColor = shopAmbientGround;
        RenderSettings.fog = false;
        RenderSettings.reflectionIntensity = shopReflectionIntensity;
        if (RenderSettings.sun != null && RenderSettings.sun.enabled) RenderSettings.sun.enabled = false;
    }

    void EndIndoorLighting()
    {
        if (!indoorLit) return;
        indoorLit = false;
        RenderSettings.ambientMode = savedMode;
        RenderSettings.ambientSkyColor = savedSky;
        RenderSettings.ambientEquatorColor = savedEq;
        RenderSettings.ambientGroundColor = savedGround;
        RenderSettings.fog = savedFog;
        RenderSettings.reflectionIntensity = savedRefl;
        if (savedSun != null) savedSun.enabled = savedSunEnabled || savedSun.intensity > 0.002f;
    }

    // ------------------------------------------------------------------ colours

    void TintTrucks(DepotRowNet net)
    {
        var nm = NetworkManager.Singleton;
        if (nm == null || nm.SpawnManager == null) return;
        for (int i = 0; i < DepotRowLayout.LotCount; i++)
        {
            if (!net.TryGetLot(i, out var s) || !s.Claimed || s.truckId == 0) continue;
            if (!nm.SpawnManager.SpawnedObjects.TryGetValue(s.truckId, out var no) || no == null) continue;
            var paint = no.GetComponent<DepotTruckPaint>();
            if (paint == null) paint = no.gameObject.AddComponent<DepotTruckPaint>();
            paint.Apply(i, s.color);
            var tag = no.GetComponent<DepotTruckNameTag>();
            if (tag == null) tag = no.gameObject.AddComponent<DepotTruckNameTag>();
            tag.Apply(i);
        }
    }

    void TintPlayers(DepotRowNet net)
    {
        players.Clear();
        players.AddRange(FindObjectsByType<PlayerController>(FindObjectsSortMode.None));
        foreach (var p in players)
        {
            if (p == null || !p.IsSpawned) continue;
            var puppet = p.GetComponent<ChuckPuppet>();
            if (puppet == null) continue;
            int lot = net.LotOwnedBy(p.OwnerClientId);
            if (lot >= 0 && net.TryGetLot(lot, out var s)) puppet.SetPlayerColor(DepotColors.Get(s.color));
            else puppet.SetPlayerColor(null);
        }
    }

    // ------------------------------------------------------------------ the old depot

    /// <summary>Remove the obsolete closed garage facade. The host has a real, usable business on Depot Row.</summary>
    void MakeTownYard()
    {
        foreach (var d in FindObjectsByType<GarageDoorController>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            if (d == null || d.depotLot >= 0 || d.gameObject.scene == gameObject.scene) continue;
            var depot = d.transform.parent;
            if (depot == null) continue;
            // Keep the underlying road/pad and shared spawn markers. No dead door or misleading arrow remains.
            var building = depot.Find("Building");
            if (building != null) building.gameObject.SetActive(false);
            var paint = depot.Find("Paint");
            if (paint != null) paint.gameObject.SetActive(false);
            d.gameObject.SetActive(false);
        }
    }
}
