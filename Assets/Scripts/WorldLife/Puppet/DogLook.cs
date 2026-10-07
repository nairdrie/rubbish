using UnityEngine;
using UnityEngine.Rendering;

/// <summary>Dog breeds (§3.1): the dog walkers' sausage dogs and corgis, the Pearl Lady's poodle.</summary>
public enum DogBreed : byte { Sausage = 0, Corgi = 1, Poodle = 2 }

public struct DogStyle
{
    public DogBreed breed;
    public Color coat, belly, ears;
    public string name;
    public float barkPitch;
}

/// <summary>
/// WorldLife package C (Docs/WorldLife.md §3.1, §8.2): the dogs, built from primitives with googly eyes, plus the leash
/// (a 2-point LineRenderer). Like <see cref="PuppetRig"/>, one universal rig per pooled dog, restyled per breed on
/// assignment. Dogs are never hit: they leap clear (<see cref="DogAgent"/>).
/// </summary>
public sealed class DogRig
{
    public enum Slot : byte { Body, Fluff, Collar, Head, Snout, Nose, EarL, EarR, EyeL, PupilL, EyeR, PupilR, LegFL, LegFR, LegBL, LegBR, Tail, Pom, Count }

    public GameObject go;
    public Transform root, visual, bodyPivot, headPivot, tailPivot, collar;
    public readonly Transform[] legs = new Transform[4];
    public readonly MeshRenderer[] r = new MeshRenderer[(int)Slot.Count];
    public readonly MeshFilter[] f = new MeshFilter[(int)Slot.Count];
    public LineRenderer leash;
    public bool IsLite { get; private set; }
    public float bodyY, legLen, length;
    readonly bool[] on = new bool[(int)Slot.Count];

    static Material leashMat;
    static readonly string[] Names = { "Biscuit", "Waffles", "Sir Barksalot", "Peanut", "Noodle", "Muffin", "Princess Fluffington", "Rex", "Gizmo", "Pickles" };

    public static DogStyle StyleFor(DogBreed breed, ulong hash)
    {
        var d = new PuppetDice(hash);
        var s = new DogStyle { breed = breed, name = d.Pick(Names), barkPitch = d.Range(0.9f, 1.4f) };
        switch (breed)
        {
            case DogBreed.Corgi: s.coat = new Color(0.86f, 0.52f, 0.22f); s.belly = new Color(0.98f, 0.95f, 0.9f); s.ears = s.coat; break;
            case DogBreed.Poodle:
                s.coat = d.Value < 0.5f ? new Color(0.98f, 0.97f, 0.96f) : new Color(1f, 0.74f, 0.84f);
                s.belly = s.coat; s.ears = s.coat; s.barkPitch = d.Range(1.4f, 1.8f); s.name = d.Value < 0.5f ? "Princess Fluffington" : "Gigi";
                break;
            default: s.coat = d.Value < 0.5f ? new Color(0.45f, 0.26f, 0.13f) : new Color(0.12f, 0.1f, 0.09f); s.belly = new Color(0.66f, 0.45f, 0.25f); s.ears = s.coat * 0.85f; break;
        }
        s.coat.a = s.belly.a = s.ears.a = 1f;
        return s;
    }

    public static DogRig Build(Transform parent, string name)
    {
        var g = new DogRig();
        g.go = new GameObject(name);
        g.go.layer = PuppetKit.Layer;
        g.root = g.go.transform;
        g.root.SetParent(parent, false);
        g.visual = PuppetKit.Node(g.root, "Visual");
        g.bodyPivot = PuppetKit.Node(g.visual, "Body");
        g.headPivot = PuppetKit.Node(g.bodyPivot, "Head");
        g.tailPivot = PuppetKit.Node(g.bodyPivot, "Tail");
        g.collar = PuppetKit.Node(g.headPivot, "LeashPoint");
        for (int i = 0; i < 4; i++) g.legs[i] = PuppetKit.Node(g.bodyPivot, "Leg" + i);
        for (int i = 0; i < (int)Slot.Count; i++)
        {
            var s = (Slot)i;
            Transform p = s switch
            {
                Slot.Head or Slot.Snout or Slot.Nose or Slot.EarL or Slot.EarR or Slot.EyeL or Slot.EyeR => g.headPivot,
                Slot.PupilL => g.r[(int)Slot.EyeL].transform,
                Slot.PupilR => g.r[(int)Slot.EyeR].transform,
                Slot.LegFL => g.legs[0], Slot.LegFR => g.legs[1], Slot.LegBL => g.legs[2], Slot.LegBR => g.legs[3],
                Slot.Tail or Slot.Pom => g.tailPivot,
                _ => g.bodyPivot,
            };
            g.r[i] = PuppetKit.Part(p, s.ToString(), out g.f[i]);
            g.r[i].gameObject.SetActive(false);
        }
        g.leash = g.go.AddComponent<LineRenderer>();
        g.leash.positionCount = 2;
        g.leash.useWorldSpace = true;
        g.leash.widthMultiplier = 0.025f;
        g.leash.numCapVertices = 0;
        g.leash.shadowCastingMode = ShadowCastingMode.Off;
        g.leash.receiveShadows = false;
        if (leashMat == null) leashMat = PuppetKit.Unlit(new Color(0.85f, 0.18f, 0.2f));
        g.leash.sharedMaterial = leashMat;
        g.leash.enabled = false;
        return g;
    }

    public void Apply(in DogStyle st)
    {
        float L, h, legL, w, headS;
        switch (st.breed)
        {
            case DogBreed.Corgi: L = 0.5f; h = 0.24f; legL = 0.11f; w = 0.24f; headS = 0.24f; break;
            case DogBreed.Poodle: L = 0.38f; h = 0.26f; legL = 0.27f; w = 0.2f; headS = 0.2f; break;
            default: L = 0.62f; h = 0.18f; legL = 0.08f; w = 0.17f; headS = 0.2f; break;
        }
        length = L; legLen = legL;
        bodyY = legL + h * 0.4f;
        bodyPivot.localPosition = new Vector3(0f, bodyY, 0f);
        headPivot.localPosition = new Vector3(0f, h * 0.45f, L * 0.5f);
        tailPivot.localPosition = new Vector3(0f, h * 0.2f, -L * 0.5f);
        collar.localPosition = new Vector3(0f, -headS * 0.25f, -headS * 0.2f);
        Vector3[] legPos = { new Vector3(-w * 0.3f, -h * 0.2f, L * 0.32f), new Vector3(w * 0.3f, -h * 0.2f, L * 0.32f),
                             new Vector3(-w * 0.3f, -h * 0.2f, -L * 0.32f), new Vector3(w * 0.3f, -h * 0.2f, -L * 0.32f) };
        for (int i = 0; i < 4; i++) legs[i].localPosition = legPos[i];
        float legSeg = bodyY - h * 0.2f;   // pivot height above the ground

        bool poodle = st.breed == DogBreed.Poodle, corgi = st.breed == DogBreed.Corgi;
        Set(Slot.Body, PuppetKit.Shape.Capsule, st.coat, Vector3.zero, new Vector3(90f, 0f, 0f), new Vector3(w, L * 0.5f, h), true);
        Set(Slot.Fluff, PuppetKit.Shape.Sphere, poodle ? st.coat : st.belly, new Vector3(0f, poodle ? h * 0.15f : -h * 0.18f, L * 0.22f), Vector3.zero,
            poodle ? new Vector3(w * 1.5f, h * 1.6f, L * 0.55f) : new Vector3(w * 0.8f, h * 0.6f, L * 0.45f));
        Set(Slot.Collar, PuppetKit.Shape.Cylinder, new Color(0.85f, 0.18f, 0.2f), new Vector3(0f, h * 0.3f, L * 0.42f), new Vector3(70f, 0f, 0f),
            new Vector3(w * 0.85f, 0.02f, w * 0.85f));
        Set(Slot.Head, PuppetKit.Shape.Sphere, st.coat, Vector3.zero, Vector3.zero, poodle ? new Vector3(headS * 1.2f, headS * 1.3f, headS * 1.1f) : Vector3.one * headS);
        Set(Slot.Snout, PuppetKit.Shape.Capsule, st.breed == DogBreed.Sausage ? st.coat : st.belly, new Vector3(0f, -headS * 0.12f, headS * 0.5f),
            new Vector3(90f, 0f, 0f), new Vector3(headS * 0.42f, headS * (st.breed == DogBreed.Sausage ? 0.42f : 0.3f), headS * 0.36f));
        Set(Slot.Nose, PuppetKit.Shape.Sphere, new Color(0.06f, 0.05f, 0.05f), new Vector3(0f, -headS * 0.06f, headS * (st.breed == DogBreed.Sausage ? 0.98f : 0.82f)),
            Vector3.zero, Vector3.one * headS * 0.22f);
        for (int s = -1; s <= 1; s += 2)
        {
            var ear = s < 0 ? Slot.EarL : Slot.EarR;
            if (corgi) Set(ear, PuppetKit.Shape.Capsule, st.ears, new Vector3(headS * 0.3f * s, headS * 0.55f, -headS * 0.05f), new Vector3(0f, 0f, -18f * s), new Vector3(headS * 0.3f, headS * 0.32f, headS * 0.12f));
            else if (poodle) Set(ear, PuppetKit.Shape.Sphere, st.ears, new Vector3(headS * 0.6f * s, -headS * 0.15f, -headS * 0.05f), Vector3.zero, new Vector3(headS * 0.45f, headS * 0.7f, headS * 0.45f));
            else Set(ear, PuppetKit.Shape.Capsule, st.ears, new Vector3(headS * 0.48f * s, -headS * 0.15f, 0f), new Vector3(0f, 0f, 8f * s), new Vector3(headS * 0.22f, headS * 0.42f, headS * 0.1f));
            var eye = s < 0 ? Slot.EyeL : Slot.EyeR;
            Vector3 dir = new Vector3(0.38f * s, 0.25f, 0.88f).normalized;
            Set(eye, PuppetKit.Shape.Sphere, PuppetKit.EyeWhite, dir * headS * 0.46f, Vector3.zero, new Vector3(headS * 0.36f, headS * 0.36f, headS * 0.2f), 0.9f);
            Set(s < 0 ? Slot.PupilL : Slot.PupilR, PuppetKit.Shape.Sphere, PuppetKit.Pupil, new Vector3(0f, -0.1f, 0.34f), Vector3.zero, new Vector3(0.5f, 0.5f, 0.45f), 0.95f);
        }
        Slot[] legSlots = { Slot.LegFL, Slot.LegFR, Slot.LegBL, Slot.LegBR };
        for (int i = 0; i < 4; i++)
            Set(legSlots[i], PuppetKit.Shape.Capsule, poodle ? st.coat : (corgi ? st.belly : st.coat), new Vector3(0f, -legSeg * 0.5f, 0f), Vector3.zero,
                new Vector3(w * 0.28f, legSeg * 0.5f + 0.02f, w * 0.28f));
        Set(Slot.Tail, PuppetKit.Shape.Capsule, st.coat, new Vector3(0f, h * 0.18f, -0.06f), new Vector3(-50f, 0f, 0f), new Vector3(0.045f, corgi ? 0.04f : 0.11f, 0.045f));
        if (poodle) Set(Slot.Pom, PuppetKit.Shape.Sphere, st.coat, new Vector3(0f, h * 0.38f, -0.12f), Vector3.zero, Vector3.one * 0.11f);
        else Off(Slot.Pom);
        IsLite = false;
    }

    void Set(Slot s, PuppetKit.Shape shape, Color c, Vector3 pos, Vector3 euler, Vector3 scale, float smooth = 0.2f) =>
        Set(s, shape, c, pos, euler, scale, false, smooth);

    void Set(Slot s, PuppetKit.Shape shape, Color c, Vector3 pos, Vector3 euler, Vector3 scale, bool shadow, float smooth = 0.2f)
    {
        c.a = 1f;
        var spec = new PartSpec { on = true, shape = shape, color = c, smooth = smooth, pos = pos, euler = euler, scale = scale, shadow = shadow };
        PuppetRig.ApplyPart(r[(int)s], f[(int)s], in spec);
        on[(int)s] = true;
    }

    void Off(Slot s) { on[(int)s] = false; r[(int)s].gameObject.SetActive(false); }

    /// <summary>Lite LOD: body and head only.</summary>
    public void SetLite(bool lite)
    {
        if (IsLite == lite) return;
        IsLite = lite;
        for (int i = 0; i < r.Length; i++)
        {
            bool keep = i == (int)Slot.Body || i == (int)Slot.Head;
            bool want = on[i] && (!lite || keep);
            if (r[i].gameObject.activeSelf != want) r[i].gameObject.SetActive(want);
        }
    }

    public void Destroy()
    {
        if (go == null) return;
        if (Application.isPlaying) Object.Destroy(go); else Object.DestroyImmediate(go);
        go = null;
    }
}
