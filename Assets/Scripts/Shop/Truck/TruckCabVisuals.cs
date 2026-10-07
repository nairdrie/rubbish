using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Renderer-only cab occupant in this machine's truck space. The real network player stays hidden at the existing seat
/// anchor; cloning only Chuck meshes avoids moving a remote NetworkTransform or enabling an invisible driver's collider.
/// Driver identity comes from the replicated seat, never truck transform ownership or the business owner.
/// </summary>
[DisallowMultipleComponent]
[DefaultExecutionOrder(1101)]
public sealed class TruckCabVisuals : MonoBehaviour
{
    public static readonly Vector3 DriverHeadMetres = new Vector3(-0.50f, 2.02f, 3.15f);
    public static readonly Vector3 DriverNameMetres = new Vector3(DriverHeadMetres.x,
        TruckControllerV2.BodyBoxMetres.max.y + 0.50f, DriverHeadMetres.z);
    public const float GlassAlpha = 0.12f;

    TruckControllerV2 truck;
    Transform cab, occupant, head, wheel;
    readonly List<Material> glassMaterials = new List<Material>();
    readonly Dictionary<Renderer, Material[]> originalMaterials = new Dictionary<Renderer, Material[]>();
    ulong shownDriver = ulong.MaxValue;
    PlayerController shownPlayer;
    float nextRefresh;
    Vector3 headRest;
    WheelCollider steeringWheel;
    public ulong ShownDriver => shownDriver;
    public int GlassSlots => glassMaterials.Count;
    public bool OccupantVisible => occupant != null && occupant.gameObject.activeSelf;

    public static TruckCabVisuals Ensure(TruckControllerV2 truck)
    {
        if (truck == null) return null;
        return truck.GetComponent<TruckCabVisuals>() ?? truck.gameObject.AddComponent<TruckCabVisuals>();
    }

    public static bool IsGlassSlot(string rendererName, int slot, string materialName)
    {
        if (rendererName != "TruckBody" || string.IsNullOrEmpty(materialName)) return false;
        // Verified from TruckBody.fbx: material slot 2 is the six glass quads, despite its generic export name.
        return slot == 2 && materialName.StartsWith("Material.004", StringComparison.Ordinal) ||
            materialName.IndexOf("glass", StringComparison.OrdinalIgnoreCase) >= 0 ||
            materialName.IndexOf("windscreen", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    public static bool TryDriverHead(ulong driver, out Vector3 at, out TruckControllerV2 vehicle)
    {
        foreach (var t in TruckControllerV2.All)
        {
            if (t == null || !t.HasDriver || t.DriverClientId != driver) continue;
            at = ShopModKit.TruckPoint(t.transform, DriverHeadMetres);
            vehicle = t;
            return true;
        }
        at = default;
        vehicle = null;
        return false;
    }

    void Awake()
    {
        truck = GetComponent<TruckControllerV2>();
        MakeGlassClear();
        cab = ShopModKit.MetreRoot(transform, "Cab presentation");
        var dark = ShopModKit.Lit("truck_cab_seat", new Color(0.09f, 0.10f, 0.12f), 0.3f);
        ShopModKit.Prim(PrimitiveType.Cube, "Driver seat back", cab, dark, new Vector3(-0.50f, 1.48f, 2.91f), new Vector3(0.61f, 0.72f, 0.13f));
        ShopModKit.Prim(PrimitiveType.Cube, "Driver seat", cab, dark, new Vector3(-0.50f, 1.18f, 3.13f), new Vector3(0.61f, 0.13f, 0.55f));
        wheel = ShopModKit.Pivot("Steering wheel", cab, new Vector3(-0.50f, 1.70f, 3.42f));
        wheel.localRotation = Quaternion.Euler(58f, 0f, 0f);
        for (int i = 0; i < 12; i++)
        {
            float a = i * Mathf.PI * 2f / 12f, b = (i + 1) * Mathf.PI * 2f / 12f;
            Segment(wheel, "Wheel rim", dark, new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * 0.19f,
                new Vector3(Mathf.Cos(b), 0f, Mathf.Sin(b)) * 0.19f, 0.028f);
        }
        Segment(wheel, "Wheel spoke", dark, new Vector3(-0.18f, 0f, 0f), new Vector3(0.18f, 0f, 0f), 0.035f);
        foreach (var w in GetComponentsInChildren<WheelControl>(true))
            if (w.steerable) { steeringWheel = w.GetComponent<WheelCollider>(); break; }
    }

    void MakeGlassClear()
    {
        foreach (var renderer in GetComponentsInChildren<MeshRenderer>(true))
        {
            var mats = renderer.sharedMaterials;
            Material[] original = null;
            for (int i = 0; i < mats.Length; i++)
            {
                if (mats[i] == null || !IsGlassSlot(renderer.name, i, mats[i].name)) continue;
                if (original == null) original = (Material[])mats.Clone();
                var glass = new Material(mats[i]) { name = "Clear cab glass (runtime)", renderQueue = (int)RenderQueue.Transparent };
                var tint = new Color(0.74f, 0.88f, 0.94f, GlassAlpha);
                glass.SetOverrideTag("RenderType", "Transparent");
                if (glass.HasProperty("_BaseColor")) glass.SetColor("_BaseColor", tint);
                if (glass.HasProperty("_Color")) glass.SetColor("_Color", tint);
                if (glass.HasProperty("_Surface")) glass.SetFloat("_Surface", 1f);
                if (glass.HasProperty("_Mode")) glass.SetFloat("_Mode", 3f);
                if (glass.HasProperty("_Blend")) glass.SetFloat("_Blend", 0f);
                if (glass.HasProperty("_SrcBlend")) glass.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                if (glass.HasProperty("_DstBlend")) glass.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
                if (glass.HasProperty("_ZWrite")) glass.SetFloat("_ZWrite", 0f);
                if (glass.HasProperty("_Smoothness")) glass.SetFloat("_Smoothness", 0.45f);
                glass.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                glass.EnableKeyword("_ALPHABLEND_ON");
                glass.DisableKeyword("_ALPHATEST_ON");
                glass.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                glass.SetShaderPassEnabled("ShadowCaster", false);
                glassMaterials.Add(glass);
                mats[i] = glass;
            }
            if (original == null) continue;
            originalMaterials[renderer] = original;
            renderer.sharedMaterials = mats;
        }
    }

    void LateUpdate()
    {
        if (truck == null) return;
        if (Time.unscaledTime >= nextRefresh)
        {
            nextRefresh = Time.unscaledTime + 0.15f;
            ulong driver = truck.HasDriver ? truck.DriverClientId : ulong.MaxValue;
            var source = driver != ulong.MaxValue ? PlayerTells.For(driver)?.Player : null;
            if (driver == ulong.MaxValue && truck.IsLocalPlayerSeated)
            {
                source = PlayerController.Local; // coop:allow(local) offline-only cosmetic occupant, never gameplay identity
                if (source != null) driver = source.IsSpawned ? source.OwnerClientId : 0;
            }
            if (driver != shownDriver || source != shownPlayer || (occupant == null && source != null)) BuildOccupant(driver, source);
            if (occupant != null) occupant.gameObject.SetActive(source != null && (source.IsDriving || truck.IsLocalPlayerSeated));
        }
        if (head == null || occupant == null || !occupant.gameObject.activeSelf) return;
        float steer = steeringWheel != null ? steeringWheel.steerAngle : 0f;
        float speed = truck.Body != null ? truck.Body.linearVelocity.magnitude : 0f;
        head.localPosition = headRest + Vector3.up * (Mathf.Sin(Time.time * 7f) * Mathf.Clamp(speed * 0.002f, 0f, 0.016f));
        head.localRotation = Quaternion.Euler(0f, Mathf.Clamp(steer * 0.32f, -10f, 10f), 0f);
        wheel.localRotation = Quaternion.Euler(58f, 0f, 0f) * Quaternion.Euler(0f, steer * 2f, 0f);
    }

    void BuildOccupant(ulong client, PlayerController source)
    {
        if (occupant != null) { occupant.gameObject.SetActive(false); Destroy(occupant.gameObject); }
        occupant = head = null;
        shownDriver = client;
        shownPlayer = source;
        var puppet = source != null ? source.GetComponent<ChuckPuppet>() : null;
        if (puppet == null || puppet.parts == null || puppet.parts.Length < ChuckPartsData.Count) return;
        var face = puppet.parts[ChuckPartsData.Head_Ball];
        if (face == null) return;
        occupant = ShopModKit.Pivot("Seated driver " + client, cab, Vector3.zero);
        head = ShopModKit.Pivot("Driver body and face", occupant, DriverHeadMetres);
        headRest = DriverHeadMetres;
        int variant = puppet.Variant >= 0 ? puppet.Variant : (int)(client % 4);
        Quaternion inverse = Quaternion.Inverse(source.transform.rotation);
        for (int i = 0; i < ChuckPartsData.Count; i++)
        {
            var def = ChuckPartsData.Parts[i];
            if (def.bone != "Head" && (i < ChuckPartsData.Shirt_Chest || i > ChuckPartsData.Duck_Beak)) continue;
            if (!PartVisible(i, variant)) continue;
            var original = puppet.parts[i];
            var mesh = original != null ? original.GetComponent<MeshFilter>() : null;
            var renderer = original != null ? original.GetComponent<MeshRenderer>() : null;
            if (mesh == null || mesh.sharedMesh == null || renderer == null) continue;
            var copy = new GameObject(original.name).transform;
            copy.SetParent(head, false);
            copy.localPosition = inverse * (original.position - face.position);
            copy.localRotation = inverse * original.rotation;
            copy.localScale = original.lossyScale;
            copy.gameObject.AddComponent<MeshFilter>().sharedMesh = mesh.sharedMesh;
            var shown = copy.gameObject.AddComponent<MeshRenderer>();
            shown.sharedMaterials = renderer.sharedMaterials;
            shown.shadowCastingMode = ShadowCastingMode.Off;
            var tint = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(tint);
            shown.SetPropertyBlock(tint);
        }
        var shirt = PartMaterial(puppet, ChuckPartsData.Shirt_Chest);
        var glove = PartMaterial(puppet, ChuckPartsData.Glove_Mitt_L);
        for (int side = -1; side <= 1; side += 2)
        {
            var shoulder = new Vector3(-0.50f + side * 0.24f, 1.73f, 3.12f);
            var hand = new Vector3(-0.50f + side * 0.15f, 1.70f, 3.38f);
            Segment(occupant, "Sleeve", shirt, shoulder, hand, 0.13f);
            ShopModKit.Prim(PrimitiveType.Sphere, "Hand on wheel", occupant, glove, hand, new Vector3(0.13f, 0.14f, 0.14f));
        }
    }

    static Material PartMaterial(ChuckPuppet p, int index)
    {
        var t = p.parts[index];
        var r = t != null ? t.GetComponent<Renderer>() : null;
        return r != null ? r.sharedMaterial : ShopModKit.Lit("truck_cab_fallback", Color.gray);
    }

    public static bool PartVisible(int index, int variant)
    {
        if (index < 0 || index >= ChuckPartsData.Count) return false;
        var look = ChuckPartsData.Variants[Mathf.Clamp(variant, 0, 3)];
        for (int i = index; i >= 0; i = ChuckPartsData.Parts[i].parent)
        {
            if (Array.IndexOf(look.off, i) >= 0) return false;
            byte only = ChuckPartsData.Parts[i].variantOnly;
            if (only != 0 && only != variant + 1 && Array.IndexOf(look.on, i) < 0) return false;
        }
        return true;
    }

    static void Segment(Transform parent, string name, Material material, Vector3 a, Vector3 b, float width)
    {
        Vector3 delta = b - a;
        ShopModKit.Prim(PrimitiveType.Cylinder, name, parent, material, (a + b) * 0.5f,
            new Vector3(width, delta.magnitude * 0.5f, width), Quaternion.FromToRotation(Vector3.up, delta).eulerAngles);
    }

    void OnDisable()
    {
        if (occupant != null) occupant.gameObject.SetActive(false);
    }

    void OnDestroy()
    {
        foreach (var pair in originalMaterials) if (pair.Key != null) pair.Key.sharedMaterials = pair.Value;
        foreach (var material in glassMaterials) if (material != null) Destroy(material);
        if (cab != null) Destroy(cab.gameObject);
    }
}
