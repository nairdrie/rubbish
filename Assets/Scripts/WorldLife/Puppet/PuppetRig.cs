using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// WorldLife package C (Docs/WorldLife.md §3.1, §4.2): the universal Neighbour Puppet rig. Built ONCE per pooled puppet
/// (every <see cref="PartSlot"/> exists), then restyled on assignment by <see cref="Apply"/> (swap meshes, shared
/// materials and local poses; switch slots on or off), so the pool never allocates after warm-up. No Animator, no
/// skinned mesh, no colliders: procedural transforms only, written by <see cref="ResidentAgent"/> through the pose
/// setters below. Lite LOD (<see cref="SetLite"/>) keeps 4 renderers: a bean body down to the feet, head, nose, hat.
///
/// Hierarchy (+Z forward, origin at the feet):
/// Root → Visual (bob, lean, flop spin) → Juice → Torso (hips; lean) → {Body, Outfit, Beam, Head (neck) → {Skull, Nose,
/// Mouth, Eyes (+pupils), Brows, Accents, HatPivot → Hat0..2}, ArmL/R (shoulder) → {Arm, Mitt, Hand → Prop, Prop2 / PropL},
/// Carry}; Juice → LegL/R (hip) → {Leg, Shoe}.
/// </summary>
public sealed class PuppetRig
{
    public GameObject go;
    public Transform root, visual, juice, torso, head, hatPivot, armL, armR, handL, handR, legL, legR, carry;
    public readonly MeshRenderer[] r = new MeshRenderer[(int)PartSlot.Count];
    public readonly MeshFilter[] f = new MeshFilter[(int)PartSlot.Count];
    public readonly Transform[] t = new Transform[(int)PartSlot.Count];
    readonly bool[] on = new bool[(int)PartSlot.Count];
    readonly bool[] liteKeep = new bool[(int)PartSlot.Count];

    public RigLayout layout;
    public Color skin;
    public bool IsLite { get; private set; }
    public bool Visible { get; private set; } = true;
    PartSpec bodySpec;
    Material skinMat, noseMat;
    int angerStep;
    bool browsOn;

    // googly pupils (eye-local offsets + velocities)
    Vector2 pupL, pupR, pupLv, pupRv;
    Vector3 lastHeadPos;
    float blinkT, nextBlink;
    float eyeScaleY0;

    /// <summary>Build every slot once (renderers off until <see cref="Apply"/>).</summary>
    public static PuppetRig Build(Transform parent, string name)
    {
        var g = new PuppetRig();
        g.go = new GameObject(name);
        g.go.layer = PuppetKit.Layer;
        g.root = g.go.transform;
        g.root.SetParent(parent, false);
        g.visual = PuppetKit.Node(g.root, "Visual");
        g.juice = PuppetKit.Node(g.visual, "Juice");
        g.torso = PuppetKit.Node(g.juice, "Torso");
        g.head = PuppetKit.Node(g.torso, "Head");
        g.hatPivot = PuppetKit.Node(g.head, "HatPivot");
        g.armL = PuppetKit.Node(g.torso, "ArmL");
        g.armR = PuppetKit.Node(g.torso, "ArmR");
        g.handL = PuppetKit.Node(g.armL, "HandL");
        g.handR = PuppetKit.Node(g.armR, "HandR");
        g.legL = PuppetKit.Node(g.juice, "LegL");
        g.legR = PuppetKit.Node(g.juice, "LegR");
        g.carry = PuppetKit.Node(g.torso, "Carry");
        for (int i = 0; i < (int)PartSlot.Count; i++)
        {
            var slot = (PartSlot)i;
            var rr = PuppetKit.Part(g.ParentOf(slot), slot.ToString(), out var ff);
            g.r[i] = rr; g.f[i] = ff; g.t[i] = rr.transform;
            rr.gameObject.SetActive(false);
        }
        return g;
    }

    Transform ParentOf(PartSlot s)
    {
        switch (s)
        {
            case PartSlot.Body: case PartSlot.Outfit: case PartSlot.Beam: return torso;
            case PartSlot.Skull: case PartSlot.Nose: case PartSlot.Mouth: case PartSlot.EyeL: case PartSlot.EyeR:
            case PartSlot.BrowL: case PartSlot.BrowR: case PartSlot.Accent: case PartSlot.Accent2: return head;
            case PartSlot.PupilL: return t[(int)PartSlot.EyeL];
            case PartSlot.PupilR: return t[(int)PartSlot.EyeR];
            case PartSlot.Hat0: case PartSlot.Hat1: case PartSlot.Hat2: return hatPivot;
            case PartSlot.ArmL: case PartSlot.MittL: return armL;
            case PartSlot.ArmR: case PartSlot.MittR: return armR;
            case PartSlot.LegL: case PartSlot.ShoeL: return legL;
            case PartSlot.LegR: case PartSlot.ShoeR: return legR;
            case PartSlot.Prop: case PartSlot.Prop2: return handR;
            case PartSlot.PropL: return handL;
        }
        return torso;
    }

    // ------------------------------------------------------------------ restyle

    /// <summary>Restyle to a composed spec (assignment time; no allocation once the colour materials exist).</summary>
    public void Apply(RigSpec spec)
    {
        layout = spec.layout;
        skin = spec.skin;
        var L = spec.layout;
        torso.localPosition = new Vector3(0f, L.hipY, 0f);
        head.localPosition = new Vector3(0f, L.neckY - L.hipY, 0f);
        hatPivot.localPosition = new Vector3(0f, 0.72f * L.headR + L.headR, 0f);
        armL.localPosition = new Vector3(-L.shoulderX, L.shoulderY - L.hipY, 0f);
        armR.localPosition = new Vector3(L.shoulderX, L.shoulderY - L.hipY, 0f);
        handL.localPosition = L.hand;
        handR.localPosition = L.hand;
        legL.localPosition = new Vector3(-L.legX, L.hipY, 0f);
        legR.localPosition = new Vector3(L.legX, L.hipY, 0f);
        carry.localPosition = L.carry;
        carry.localRotation = Quaternion.identity;
        for (int i = 0; i < (int)PartSlot.Count; i++)
        {
            ref var p = ref spec.parts[i];
            on[i] = p.on;
            liteKeep[i] = p.lite;
            if (p.on) ApplyPart(r[i], f[i], in p);
            else if (r[i].gameObject.activeSelf) r[i].gameObject.SetActive(false);
        }
        bodySpec = spec.parts[(int)PartSlot.Body];
        skinMat = r[(int)PartSlot.Skull].sharedMaterial;
        noseMat = r[(int)PartSlot.Nose].sharedMaterial;
        eyeScaleY0 = spec.parts[(int)PartSlot.EyeL].scale.y;
        angerStep = 0;
        browsOn = false;
        IsLite = false;
        pupL = pupR = new Vector2(0f, -0.1f);
        pupLv = pupRv = Vector2.zero;
        blinkT = 0f;
        ResetPose();
    }

    /// <summary>Shape, material and local pose of one part from its spec.</summary>
    public static void ApplyPart(MeshRenderer rr, MeshFilter ff, in PartSpec p)
    {
        ff.sharedMesh = PuppetKit.MeshOf(p.shape);
        rr.sharedMaterial = p.matKind == 2 ? PuppetKit.Beam(p.color) : p.matKind == 1 ? PuppetKit.Unlit(p.color) : PuppetKit.Lit(p.color, p.smooth, p.emission);
        rr.shadowCastingMode = p.shadow ? ShadowCastingMode.On : ShadowCastingMode.Off;
        var tt = rr.transform;
        tt.localPosition = p.pos;
        tt.localRotation = Quaternion.Euler(p.euler);
        tt.localScale = p.scale;
        if (!rr.gameObject.activeSelf) rr.gameObject.SetActive(true);
    }

    /// <summary>Lite LOD (35–90 m): body (a bean down to the feet), head, nose and the main hat. Full: everything.</summary>
    public void SetLite(bool lite)
    {
        if (IsLite == lite) return;
        IsLite = lite;
        for (int i = 0; i < (int)PartSlot.Count; i++)
        {
            if (i == (int)PartSlot.Body) continue;
            bool isBrow = i == (int)PartSlot.BrowL || i == (int)PartSlot.BrowR;
            bool want = isBrow ? browsOn && !lite && on[i] : on[i] && (!lite || liteKeep[i]);
            if (r[i].gameObject.activeSelf != want) r[i].gameObject.SetActive(want);
        }
        var bt = t[(int)PartSlot.Body];
        if (lite)
        {
            f[(int)PartSlot.Body].sharedMesh = PuppetKit.MeshOf(layout.liteShape);
            bt.localPosition = layout.litePos;
            bt.localScale = layout.liteScale;
            bt.localRotation = Quaternion.identity;
            r[(int)PartSlot.Body].shadowCastingMode = ShadowCastingMode.Off;
            r[(int)PartSlot.Skull].shadowCastingMode = ShadowCastingMode.Off;
        }
        else
        {
            f[(int)PartSlot.Body].sharedMesh = PuppetKit.MeshOf(bodySpec.shape);
            bt.localPosition = bodySpec.pos;
            bt.localScale = bodySpec.scale;
            bt.localRotation = Quaternion.Euler(bodySpec.euler);
            r[(int)PartSlot.Body].shadowCastingMode = ShadowCastingMode.On;
            r[(int)PartSlot.Skull].shadowCastingMode = ShadowCastingMode.On;
        }
    }

    public void SetVisible(bool v)
    {
        if (Visible == v) return;
        Visible = v;
        visual.gameObject.SetActive(v);
    }

    /// <summary>How many renderers are switched on right now (the §4.4 renderer budget).</summary>
    public int ActiveRenderers
    {
        get
        {
            if (!Visible || !go.activeInHierarchy) return 0;
            int n = 0;
            for (int i = 0; i < r.Length; i++) if (r[i].gameObject.activeSelf) n++;
            return n;
        }
    }

    /// <summary>Show / hide the hand props (a flattened resident's mug flies off; it's back when they rejoin).</summary>
    public void SetPropVisible(bool v)
    {
        for (int k = 0; k < 3; k++)
        {
            int i = k == 0 ? (int)PartSlot.Prop : k == 1 ? (int)PartSlot.Prop2 : (int)PartSlot.PropL;
            bool want = v && on[i] && !IsLite;
            if (r[i].gameObject.activeSelf != want) r[i].gameObject.SetActive(want);
        }
    }

    public bool PartOn(PartSlot s) => on[(int)s];

    // ------------------------------------------------------------------ face

    /// <summary>0..3: the face (skull + nose) pushed toward red, quantised so materials stay shared.</summary>
    public void SetAngry(int step)
    {
        step = Mathf.Clamp(step, 0, 3);
        if (step == angerStep) return;
        angerStep = step;
        if (step == 0)
        {
            r[(int)PartSlot.Skull].sharedMaterial = skinMat;
            r[(int)PartSlot.Nose].sharedMaterial = noseMat;
        }
        else
        {
            var m = PuppetKit.Lit(PuppetKit.AngryTint(skin, step), 0.25f);
            r[(int)PartSlot.Skull].sharedMaterial = m;
            r[(int)PartSlot.Nose].sharedMaterial = m;
        }
    }

    /// <summary>Brows on (angry / startled) with a slam angle (−1 sad .. 0 neutral .. 1 furious).</summary>
    public void SetBrows(bool show, float angry)
    {
        bool want = show && !IsLite;
        browsOn = show;
        var bl = t[(int)PartSlot.BrowL]; var br = t[(int)PartSlot.BrowR];
        if (bl.gameObject.activeSelf != want) { bl.gameObject.SetActive(want); br.gameObject.SetActive(want); }
        if (!want) return;
        float R = layout.headR;
        float z = 12f + angry * 22f;
        float y = (0.72f + 0.5f - 0.1f * Mathf.Max(0f, angry)) * R;
        bl.localPosition = new Vector3(-0.36f * R, y, 0.86f * R);
        br.localPosition = new Vector3(0.36f * R, y, 0.86f * R);
        bl.localRotation = Quaternion.Euler(-20f, 0f, -z);
        br.localRotation = Quaternion.Euler(-20f, 0f, z);
    }

    /// <summary>Mouth 0 = flat line, 1 = a big "O".</summary>
    public void SetMouth(float open, float wide = 1f)
    {
        var m = t[(int)PartSlot.Mouth];
        float R = layout.headR;
        open = Mathf.Clamp01(open);
        m.localScale = new Vector3(Mathf.Lerp(0.38f, 0.26f, open) * R * wide, Mathf.Lerp(0.06f, 0.3f, open) * R, 0.08f * R);
    }

    /// <summary>Rattle the pupils (bonks, surprises).</summary>
    public void KickEyes(float s, ref PuppetDice dice)
    {
        pupLv += dice.InCircle * s;
        pupRv += dice.InCircle * s;
    }

    /// <summary>Googly pupils on springs that sag under gravity and rattle with head motion; blinks. Full LOD only.</summary>
    public void TickEyes(float dt, ref PuppetDice dice)
    {
        if (IsLite || dt <= 0f) return;
        Vector3 hp = head.position;
        Vector3 motion = (lastHeadPos - hp);
        lastHeadPos = hp;
        if (motion.sqrMagnitude > 1f) motion = Vector3.zero;   // teleport / first frame
        var eL = t[(int)PartSlot.EyeL];
        Vector3 g = eL.InverseTransformDirection(Vector3.down) * 7f;
        Vector3 mv = eL.InverseTransformVector(motion) * 40f;
        Vector2 push = new Vector2(g.x, g.y) * dt + new Vector2(mv.x, mv.y);
        StepPupil(ref pupL, ref pupLv, push, dt);
        StepPupil(ref pupR, ref pupRv, push, dt);
        t[(int)PartSlot.PupilL].localPosition = new Vector3(pupL.x, pupL.y, 0.34f);
        t[(int)PartSlot.PupilR].localPosition = new Vector3(pupR.x, pupR.y, 0.34f);

        // Blink: squash the eyes for 0.12 s every 2.5–6 s.
        nextBlink -= dt;
        if (nextBlink <= 0f) { blinkT = 0.12f; nextBlink = dice.Range(2.5f, 6f); }
        float open = 1f;
        if (blinkT > 0f) { blinkT -= dt; open = 0.15f; }
        var sL = t[(int)PartSlot.EyeL].localScale;
        if (Mathf.Abs(sL.y - eyeScaleY0 * open) > 1e-4f)
        {
            sL.y = eyeScaleY0 * open;
            t[(int)PartSlot.EyeL].localScale = sL;
            var sR = t[(int)PartSlot.EyeR].localScale; sR.y = sL.y;
            t[(int)PartSlot.EyeR].localScale = sR;
        }
    }

    static void StepPupil(ref Vector2 o, ref Vector2 v, Vector2 push, float dt)
    {
        v += push;
        v *= Mathf.Clamp01(1f - dt * 1.6f);
        o += v * dt;
        const float max = 0.22f;
        float m = o.magnitude;
        if (m > max)
        {
            Vector2 n = o / m;
            o = n * max;
            v = Vector2.Reflect(v, -n) * 0.5f;
        }
    }

    // ------------------------------------------------------------------ pose

    public void ResetPose()
    {
        visual.localPosition = Vector3.zero;
        visual.localRotation = Quaternion.identity;
        visual.localScale = Vector3.one;
        juice.localScale = Vector3.one;
        torso.localRotation = Quaternion.identity;
        head.localRotation = Quaternion.identity;
        armL.localRotation = Quaternion.Euler(0f, 0f, -10f);
        armR.localRotation = Quaternion.Euler(0f, 0f, 10f);
        legL.localRotation = Quaternion.identity;
        legR.localRotation = Quaternion.identity;
        SetMouth(0f);
        lastHeadPos = head.position;
    }

    public void Place(Vector3 pos, float yaw) => root.SetPositionAndRotation(pos, Quaternion.Euler(0f, yaw, 0f));

    /// <summary>Visual offset + rotation (bob, waddle roll, the flop's spin / starfish) and squash scale.</summary>
    public void Body(Vector3 offset, Quaternion rot, float squash)
    {
        visual.localPosition = offset;
        visual.localRotation = rot;
        float s = 1f + squash;
        float side = 1f / Mathf.Sqrt(Mathf.Max(0.2f, s));
        visual.localScale = new Vector3(side, s, side);
    }

    public void Torso(float pitch, float roll, float yaw = 0f) => torso.localRotation = Quaternion.Euler(pitch, yaw, roll);
    public void Head(float pitch, float yaw, float roll) => head.localRotation = Quaternion.Euler(pitch, yaw, roll);
    /// <summary>Arm angles (x = swing forward(−)/back(+), z = splay; the left arm's z is mirrored for you).</summary>
    public void Arms(float lx, float lz, float rx, float rz, float ly = 0f, float ry = 0f)
    {
        armL.localRotation = Quaternion.Euler(lx, ly, -lz);
        armR.localRotation = Quaternion.Euler(rx, ry, rz);
    }
    public void Legs(float lx, float rx, float spread = 0f)
    {
        legL.localRotation = Quaternion.Euler(lx, 0f, -spread);
        legR.localRotation = Quaternion.Euler(rx, 0f, spread);
    }

    public void Destroy()
    {
        if (go == null) return;
        if (Application.isPlaying) Object.Destroy(go); else Object.DestroyImmediate(go);
        go = null;
    }
}
