using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// A shop's dumpster (Docs/Progression.md §6.11). Built at runtime on every machine beside each commercial stop
/// (<see cref="DumpsterRuntime"/>); it owns no state. What is in it is the stop's curb records in the dumpster slots
/// (<see cref="DumpsterRules"/>), which replicate with the rest of the curb, so the lids, the bags poking out and the
/// count on the front are the same everywhere. Only a rear-loader's <see cref="TruckDumpsterLift"/> empties it.
/// </summary>
[DisallowMultipleComponent]
public class Dumpster : MonoBehaviour, IInteractable
{
    static readonly List<Dumpster> all = new List<Dumpster>();
    public static IReadOnlyList<Dumpster> All => all;

    public static Dumpster ForStop(string stopId)
    {
        for (int i = 0; i < all.Count; i++) if (all[i] != null && all[i].StopId == stopId) return all[i];
        return null;
    }

    public PickupStop Stop { get; private set; }
    public string StopId => Stop != null ? Stop.stopId : null;
    /// <summary>True while a truck has it in the air (host: nobody else may start; everyone: the collider is off).</summary>
    public bool Lifted { get; private set; }

    /// <summary>Bags in it right now, on any machine.</summary>
    public int Bags => Stop != null ? DumpsterRules.Count(CurbTrash.StatsFor(Stop.stopId).usedSlotMask) : 0;
    /// <summary>The middle of its open front, where a truck's bar hooks on.</summary>
    public Vector3 HookPoint => rest + transform.forward * 0.85f + Vector3.up * 1.1f;

    Transform body, lidLeft, lidRight;
    readonly Transform[] sacks = new Transform[DumpsterRules.Capacity];
    TextMeshPro counter;
    Collider solid;
    Vector3 rest;
    Quaternion restRotation;
    int shown = -1;
    float nextLook, lidOpen;
    Coroutine lifting;

    static readonly List<CurbBag> tmp = new List<CurbBag>();

    /// <summary>Host: this dumpster's records, oldest first.</summary>
    public void Records(List<CurbBag> into)
    {
        into.Clear();
        if (Stop == null) return;
        CurbTrash.BagsAt(Stop.stopId, tmp);
        foreach (var r in tmp) if (DumpsterRules.IsDumpsterSlot(r.slot)) into.Add(r);
        into.Sort((a, b) => a.putOutAtHours.CompareTo(b.putOutAtHours));
    }

    void OnEnable() { if (!all.Contains(this)) all.Add(this); }
    void OnDisable() => all.Remove(this);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => all.Clear();

    // ------------------------------------------------------------------ build

    public static Dumpster Build(PickupStop stop)
    {
        var go = new GameObject("Dumpster (" + stop.stopId + ")");
        go.transform.SetParent(stop.transform, false);
        Vector3 at = stop.transform.TransformPoint(DumpsterRules.PadLocal);
        if (Physics.Raycast(at + Vector3.up * 3f, Vector3.down, out var hit, 8f, 1 << SortingKit.GroundLayer, QueryTriggerInteraction.Ignore))
            at.y = hit.point.y;
        else at.y = stop.transform.position.y - 0.2f;
        go.transform.SetPositionAndRotation(at, Quaternion.LookRotation(Vector3.ProjectOnPlane(stop.transform.forward, Vector3.up).normalized, Vector3.up));
        var d = go.AddComponent<Dumpster>();
        d.Stop = stop;
        d.rest = at;
        d.restRotation = go.transform.rotation;
        d.BuildModel(PickupVisuals.StableHash(stop.stopId));
        return d;
    }

    static readonly Color[] Paints =
    {
        new Color(0.16f, 0.42f, 0.27f), new Color(0.17f, 0.33f, 0.55f), new Color(0.5f, 0.2f, 0.2f), new Color(0.3f, 0.32f, 0.36f),
    };

    void BuildModel(int seed)
    {
        int pick = (int)((uint)seed % (uint)Paints.Length);
        var paint = ShopModKit.Lit("mat/dumpster_paint_" + pick, Paints[pick], 0.35f, 0.3f);
        var dark = ShopModKit.Lit("mat/dumpster_dark", new Color(0.12f, 0.13f, 0.15f), 0.3f, 0.4f);
        var lid = ShopModKit.Lit("mat/dumpster_lid", new Color(0.09f, 0.09f, 0.1f), 0.45f);
        var steel = ShopModKit.Lit("mat/hog_belly_steel", new Color(0.36f, 0.4f, 0.45f), 0.6f, 0.7f);
        var hazard = ShopModKit.Lit("mat/hog_hazard", new Color(1f, 0.85f, 0.1f), 0.4f);
        var sack = ShopModKit.Lit("mat/dumpster_sack", new Color(0.07f, 0.07f, 0.08f), 0.5f);
        var white = ShopModKit.Lit("mat/shop_white", new Color(0.97f, 0.97f, 0.97f), 0.6f);

        body = ShopModKit.Pivot("Body", transform, Vector3.zero);
        // The tub: a wedge-topped box (low front, high back), extruded across its 2 m width
        var side = new[] { new Vector2(-0.62f, 0.16f), new Vector2(0.72f, 0.16f), new Vector2(0.85f, 1.18f), new Vector2(-0.72f, 1.5f) };
        Mesh(body, "Tub", HogMesh.Prism("dumpster_tub", side, 2.0f), paint, Vector3.zero, new Vector3(0f, -90f, 0f));
        // Ribs, rim, the trunnion bar a rear-loader hooks, the side sleeves a front-loader would use, and four casters
        foreach (float x in new[] { -0.62f, 0f, 0.62f })
            ShopModKit.Prim(PrimitiveType.Cube, "Rib", body, dark, new Vector3(x, 0.72f, 0.82f), new Vector3(0.09f, 1.0f, 0.07f), new Vector3(-7f, 0f, 0f));
        ShopModKit.Prim(PrimitiveType.Cube, "Front rim", body, dark, new Vector3(0f, 1.2f, 0.86f), new Vector3(2.08f, 0.09f, 0.1f));
        ShopModKit.Prim(PrimitiveType.Cube, "Back rim", body, dark, new Vector3(0f, 1.52f, -0.72f), new Vector3(2.08f, 0.09f, 0.1f));
        ShopModKit.Prim(PrimitiveType.Cylinder, "Trunnion bar", body, steel, new Vector3(0f, 1.05f, 0.98f), new Vector3(0.08f, 1.12f, 0.08f), new Vector3(0f, 0f, 90f));
        for (int s = -1; s <= 1; s += 2)
        {
            ShopModKit.Prim(PrimitiveType.Cube, "Bar bracket", body, dark, new Vector3(s * 0.98f, 1.05f, 0.9f), new Vector3(0.08f, 0.2f, 0.26f));
            ShopModKit.Prim(PrimitiveType.Cube, "Fork sleeve", body, dark, new Vector3(s * 1.06f, 0.82f, 0.05f), new Vector3(0.12f, 0.2f, 1.1f));
            ShopModKit.Prim(PrimitiveType.Cube, "Hazard corner", body, hazard, new Vector3(s * 0.94f, 0.4f, 0.74f), new Vector3(0.14f, 0.42f, 0.03f), new Vector3(-7f, 0f, s * 35f));
            for (int z = -1; z <= 1; z += 2)
                ShopModKit.Prim(PrimitiveType.Cylinder, "Caster", body, dark, new Vector3(s * 0.8f, 0.09f, z * 0.5f), new Vector3(0.18f, 0.05f, 0.18f), new Vector3(0f, 0f, 90f));
        }
        // Two lids hinged along the back rim; they prop open as it fills
        lidLeft = ShopModKit.Pivot("Lid hinge L", body, new Vector3(-0.5f, 1.53f, -0.7f));
        ShopModKit.Prim(PrimitiveType.Cube, "Lid", lidLeft, lid, new Vector3(0f, -0.085f, 0.79f), new Vector3(0.98f, 0.05f, 1.62f), new Vector3(11.5f, 0f, 0f));
        lidRight = ShopModKit.Pivot("Lid hinge R", body, new Vector3(0.5f, 1.53f, -0.7f));
        ShopModKit.Prim(PrimitiveType.Cube, "Lid", lidRight, lid, new Vector3(0f, -0.085f, 0.79f), new Vector3(0.98f, 0.05f, 1.62f), new Vector3(11.5f, 0f, 0f));
        // The bags that poke out of the top
        for (int i = 0; i < sacks.Length; i++)
        {
            float x = -0.6f + (i % 3) * 0.6f, z = (i / 3) * 0.55f - 0.2f;
            sacks[i] = ShopModKit.Prim(PrimitiveType.Sphere, "Sack", body, sack, new Vector3(x, 1.2f + (i / 3) * 0.2f, z), new Vector3(0.62f, 0.5f, 0.6f), new Vector3(0f, i * 47f, i * 9f));
            sacks[i].gameObject.SetActive(false);
        }
        // The plate on the front with the count
        ShopModKit.Prim(PrimitiveType.Cube, "Plate", body, white, new Vector3(0f, 0.72f, 0.815f), new Vector3(0.9f, 0.4f, 0.02f), new Vector3(-7f, 0f, 0f));
        counter = WorkshopModels.MakeLabel(body, "Count", "EMPTY", 2.2f, new Color(0.1f, 0.1f, 0.12f));
        counter.transform.localPosition = new Vector3(0f, 0.72f, 0.83f);
        counter.transform.localRotation = Quaternion.Euler(-7f, 180f, 0f);
        foreach (var r in GetComponentsInChildren<Renderer>(true)) r.gameObject.layer = 2;

        var col = new GameObject("Dumpster collider");
        col.transform.SetParent(transform, false);
        var box = col.AddComponent<BoxCollider>();
        box.center = new Vector3(0f, 0.8f, 0.05f);
        box.size = new Vector3(2.05f, 1.5f, 1.6f);
        solid = box;
        var reach = new GameObject("Dumpster prompt") { layer = Carryable.InteractableLayer };
        reach.transform.SetParent(transform, false);
        var trigger = reach.AddComponent<BoxCollider>();
        trigger.isTrigger = true;
        trigger.center = new Vector3(0f, 0.9f, 0.3f);
        trigger.size = new Vector3(2.8f, 1.8f, 2.8f);
    }

    static Transform Mesh(Transform parent, string name, Mesh mesh, Material mat, Vector3 pos, Vector3 euler)
    {
        var go = new GameObject(name) { layer = 2 };
        go.transform.SetParent(parent, false);
        go.transform.localPosition = pos;
        go.transform.localRotation = Quaternion.Euler(euler);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = mat;
        return go.transform;
    }

    // ------------------------------------------------------------------ on foot: it only explains itself

    public bool CanInteract()
    {
        var me = PlayerController.Local; // coop:allow(local) a prompt for this machine's own player
        return me != null && !me.IsDriving && !Lifted;
    }

    public string GetInteractionPrompt()
    {
        int n = Bags;
        return n > 0 ? $"Dumpster ({n} {SortingKit.Plural(n, "bag", "bags")}): back a rear-loader up to it" : "Dumpster (empty)";
    }

    public void Highlight(bool highlight) { }

    public void OnInteract(GameObject interactor)
    {
        GameHUD.Toast(Bags > 0 ? "Too heavy, and the lid's stuck. A rear-loader with the Dumpster Dunker tips the whole thing."
                               : "Empty. Smells like it remembers, though.", ToastStyle.Info, 3f);
    }

    // ------------------------------------------------------------------ presentation

    void Update()
    {
        if (Time.time >= nextLook)
        {
            nextLook = Time.time + 0.4f;
            int n = Bags;
            if (n != shown)
            {
                bool more = n > shown && shown >= 0;
                shown = n;
                for (int i = 0; i < sacks.Length; i++) sacks[i].gameObject.SetActive(i < n - 1); // the first bag is out of sight at the bottom
                counter.text = n == 0 ? "EMPTY" : n >= DumpsterRules.Capacity ? "FULL" : n + " / " + DumpsterRules.Capacity;
                if (more && !Lifted) { Juice.Punch(body, 0.06f, 0.2f); Sfx.Play("toss_in", transform.position, 0.4f, 0.8f); }
            }
        }
        float want = Lifted ? 1.6f : Mathf.Clamp01((shown - 1) / 4f);
        lidOpen = Mathf.MoveTowards(lidOpen, want, Time.deltaTime * 2.5f);
        if (lidLeft != null) lidLeft.localRotation = Quaternion.Euler(-lidOpen * 62f, 0f, 0f);
        if (lidRight != null) lidRight.localRotation = Quaternion.Euler(-lidOpen * 48f, 0f, 0f);
    }

    /// <summary>Every machine: <paramref name="truck"/> hooks the dumpster, lifts it over the hopper, pours
    /// <paramref name="bags"/> bags and puts it back. Purely visual; the host moves the records itself.</summary>
    public void PresentLift(Transform truck, Vector3 hopper, int bags, float secondsPerBag)
    {
        if (lifting != null) StopCoroutine(lifting);
        lifting = StartCoroutine(LiftAnimation(truck, hopper, Mathf.Max(0, bags), secondsPerBag));
    }

    IEnumerator LiftAnimation(Transform truck, Vector3 hopper, int bags, float secondsPerBag)
    {
        Lifted = true;
        if (solid != null) solid.enabled = false;
        Sfx.Play("beep", rest, 0.6f, 0.9f);
        // Up and over: it swings about the bar on its front rim until the open top faces down into the hopper
        Vector3 hookRest = rest + restRotation * new Vector3(0f, 1.05f, 0.98f);
        Vector3 hookUp = new Vector3(hopper.x, hopper.y + 0.75f, hopper.z) - restRotation * Vector3.forward * 0.55f;
        for (float t = 0f; t < DumpsterRules.LiftSeconds; t += Time.deltaTime)
        {
            Pose(hookRest, hookUp, Mathf.SmoothStep(0f, 1f, t / DumpsterRules.LiftSeconds));
            yield return null;
        }
        Pose(hookRest, hookUp, 1f);
        Sfx.Play("slam", hopper, 0.5f, 1.2f);
        for (int i = 0; i < bags; i++)
        {
            StartCoroutine(Tumble(hookUp + Vector3.down * 0.3f, hopper, i));
            body.localRotation = Quaternion.Euler(Mathf.Sin(Time.time * 40f) * 2.5f, 0f, 0f); // shake it out
            yield return new WaitForSeconds(Mathf.Max(0.05f, secondsPerBag));
        }
        body.localRotation = Quaternion.identity;
        yield return new WaitForSeconds(0.25f);
        for (float t = 0f; t < DumpsterRules.LowerSeconds; t += Time.deltaTime)
        {
            Pose(hookRest, hookUp, 1f - Mathf.SmoothStep(0f, 1f, t / DumpsterRules.LowerSeconds));
            yield return null;
        }
        transform.SetPositionAndRotation(rest, restRotation);
        Fx.Dust(rest, 1f);
        Sfx.Play("slam", rest, 0.6f, 0.85f);
        Juice.Punch(body, 0.1f, 0.25f);
        if (solid != null) solid.enabled = true;
        Lifted = false;
        lifting = null;
    }

    /// <summary>Place the dumpster so its front bar is on the way from its resting place to above the hopper, tipped by
    /// <paramref name="k"/> (0 = on the ground, 1 = upside down over the truck).</summary>
    void Pose(Vector3 hookRest, Vector3 hookUp, float k)
    {
        Vector3 hook = Vector3.Lerp(hookRest, hookUp, k) + Vector3.up * Mathf.Sin(k * Mathf.PI) * 0.5f;
        Quaternion rot = restRotation * Quaternion.Euler(k * 128f, 0f, 0f);
        transform.SetPositionAndRotation(hook - rot * new Vector3(0f, 1.05f, 0.98f), rot);
    }

    IEnumerator Tumble(Vector3 from, Vector3 to, int i)
    {
        var sack = ShopModKit.Prim(PrimitiveType.Sphere, "Tipped sack", null, ShopModKit.Lit("mat/dumpster_sack", new Color(0.07f, 0.07f, 0.08f), 0.5f),
            from, new Vector3(0.6f, 0.5f, 0.58f));
        sack.gameObject.layer = 2;
        Vector3 side = restRotation * Vector3.right * (((i % 3) - 1) * 0.35f);
        for (float t = 0f; t < 0.4f; t += Time.deltaTime)
        {
            if (sack == null) yield break;
            float k = t / 0.4f;
            sack.position = Vector3.Lerp(from + side, to, k) + Vector3.up * Mathf.Sin(k * Mathf.PI) * 0.35f;
            sack.Rotate(300f * Time.deltaTime, 200f * Time.deltaTime, 0f);
            yield return null;
        }
        if (sack != null) Destroy(sack.gameObject);
        Sfx.Play("toss_in", to, 0.6f, 0.9f + (i % 4) * 0.05f);
    }
}

/// <summary>Keeps a <see cref="Dumpster"/> beside every commercial stop and a <see cref="TruckDumpsterLift"/> on every
/// truck (DontDestroyOnLoad, created at startup like the Hauler's and the Hoverhog's heartbeats).</summary>
public class DumpsterRuntime : MonoBehaviour
{
    static DumpsterRuntime instance;
    float nextScan;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Boot()
    {
        if (instance != null) return;
        var go = new GameObject("[Dumpsters]");
        DontDestroyOnLoad(go);
        instance = go.AddComponent<DumpsterRuntime>();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => instance = null;

    void Update()
    {
        if (Time.unscaledTime < nextScan) return;
        nextScan = Time.unscaledTime + 1f;
        var stops = PickupStop.All;
        for (int i = 0; i < stops.Count; i++)
        {
            var stop = stops[i];
            if (stop == null || !DumpsterRules.HasDumpster(stop.districtId) || !stop.gameObject.activeInHierarchy) continue;
            if (Dumpster.ForStop(stop.stopId) == null) Dumpster.Build(stop);
        }
        var trucks = TruckControllerV2.All;
        for (int i = 0; i < trucks.Count; i++)
            if (trucks[i] != null && trucks[i].GetComponent<TruckDumpsterLift>() == null) trucks[i].gameObject.AddComponent<TruckDumpsterLift>();
    }
}
