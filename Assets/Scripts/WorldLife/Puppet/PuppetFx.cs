using TMPro;
using UnityEngine;

/// <summary>
/// WorldLife package C: the small pooled props the puppets need (no per-hit GameObject churn): dizzy stars over a
/// flattened head (like <see cref="DizzyStars"/>, but pooled), a mug / newspaper that flies off in a flop, Segways for the
/// Bluffs billionaires, and the neglect witness's hand-lettered sign. Presentation only; built under the WorldLife root
/// and dropped with it.
/// </summary>
public sealed class PuppetFx
{
    readonly Transform root;

    public PuppetFx(Transform root) { this.root = root; }

    // ================================================================== dizzy stars

    sealed class Stars
    {
        public Transform t;
        public Transform[] star = new Transform[4];
        public Transform head;
        public float until, spin, radius;
    }

    const int MaxStars = 6;
    readonly Stars[] stars = new Stars[MaxStars];
    int starCount;

    /// <summary>Orbit stars around a head for a while (re-attaching extends the time).</summary>
    public void AttachStars(Transform head, float seconds, float radius)
    {
        if (head == null) return;
        Stars s = null;
        for (int i = 0; i < starCount; i++) if (stars[i].head == head) { s = stars[i]; break; }
        if (s == null)
        {
            for (int i = 0; i < starCount; i++) if (stars[i].head == null) { s = stars[i]; break; }
            if (s == null && starCount < MaxStars) s = stars[starCount++] = BuildStars();
            if (s == null) return;
        }
        s.head = head;
        s.until = Time.time + seconds;
        s.radius = radius;
        s.t.gameObject.SetActive(true);
    }

    Stars BuildStars()
    {
        var s = new Stars { t = PuppetKit.Node(root, "DizzyStars") };
        var gold = PuppetKit.Unlit(new Color(1f, 0.88f, 0.2f));
        for (int i = 0; i < 4; i++)
        {
            var st = PuppetKit.Node(s.t, "Star");
            for (int k = 0; k < 2; k++)
            {
                var r = PuppetKit.Part(st, "Pt", out var f);
                f.sharedMesh = PuppetKit.MeshOf(PuppetKit.Shape.Cube);
                r.sharedMaterial = gold;
                r.transform.localScale = new Vector3(0.08f, 0.08f, 0.03f);
                r.transform.localRotation = Quaternion.Euler(0f, 0f, 45f * k);
            }
            s.star[i] = st;
        }
        s.t.gameObject.SetActive(false);
        return s;
    }

    /// <summary>Are stars orbiting this head right now?</summary>
    public bool StarsOn(Transform head)
    {
        if (head == null) return false;
        for (int i = 0; i < starCount; i++) if (stars[i].head == head && stars[i].t.gameObject.activeSelf) return true;
        return false;
    }

    public void DetachStars(Transform head)
    {
        for (int i = 0; i < starCount; i++)
            if (stars[i].head == head) { stars[i].head = null; stars[i].t.gameObject.SetActive(false); }
    }

    // ================================================================== flying props

    sealed class Flyer
    {
        public Transform t;
        public MeshRenderer r;
        public MeshFilter f;
        public Vector3 vel, spinAxis;
        public float age, groundY;
        public bool landed;
    }

    const int MaxFlyers = 6;
    readonly Flyer[] flyers = new Flyer[MaxFlyers];
    int flyerCount;

    /// <summary>A copy of one part (mesh, material, world pose) flies off ballistically, lands, and shrinks away.</summary>
    public void Fling(MeshRenderer src, MeshFilter srcF, Vector3 vel, ref PuppetDice dice)
    {
        if (src == null || !src.gameObject.activeInHierarchy) return;
        Flyer f = null;
        for (int i = 0; i < flyerCount; i++) if (!flyers[i].t.gameObject.activeSelf) { f = flyers[i]; break; }
        if (f == null && flyerCount < MaxFlyers)
        {
            f = flyers[flyerCount++] = new Flyer();
            f.r = PuppetKit.Part(root, "FlyingProp", out f.f);
            f.t = f.r.transform;
        }
        if (f == null) return;
        f.f.sharedMesh = srcF.sharedMesh;
        f.r.sharedMaterial = src.sharedMaterial;
        f.t.SetPositionAndRotation(src.transform.position, src.transform.rotation);
        f.t.localScale = src.transform.lossyScale;
        f.vel = vel * 0.6f + Vector3.up * 2.5f + new Vector3(dice.Range(-1f, 1f), 0f, dice.Range(-1f, 1f));
        f.spinAxis = dice.OnSphere;
        f.age = 0f;
        f.landed = false;
        f.groundY = src.transform.position.y - 1.5f;
        if (Physics.Raycast(src.transform.position + Vector3.up, Vector3.down, out var g, 6f, 1 << 6, QueryTriggerInteraction.Ignore)) f.groundY = g.point.y;
        f.t.gameObject.SetActive(true);
    }

    // ================================================================== segways (Bluffs billionaires)

    const int MaxSegways = 4;
    readonly Transform[] segways = new Transform[MaxSegways];
    readonly bool[] segwayUsed = new bool[MaxSegways];
    int segwayCount;

    public Transform TakeSegway()
    {
        for (int i = 0; i < segwayCount; i++) if (!segwayUsed[i]) { segwayUsed[i] = true; segways[i].gameObject.SetActive(true); return segways[i]; }
        if (segwayCount >= MaxSegways) return null;
        var t = PuppetKit.Node(root, "Segway");
        var black = new Color(0.1f, 0.1f, 0.11f); var silver = new Color(0.8f, 0.82f, 0.86f);
        Add(t, PuppetKit.Shape.Cube, black, new Vector3(0f, 0.09f, 0.02f), Vector3.zero, new Vector3(0.5f, 0.07f, 0.32f));
        Add(t, PuppetKit.Shape.Cylinder, black, new Vector3(-0.3f, 0.12f, 0.02f), new Vector3(0f, 0f, 90f), new Vector3(0.24f, 0.05f, 0.24f));
        Add(t, PuppetKit.Shape.Cylinder, black, new Vector3(0.3f, 0.12f, 0.02f), new Vector3(0f, 0f, 90f), new Vector3(0.24f, 0.05f, 0.24f));
        Add(t, PuppetKit.Shape.Cylinder, silver, new Vector3(0f, 0.6f, 0.2f), new Vector3(-12f, 0f, 0f), new Vector3(0.05f, 0.5f, 0.05f));
        Add(t, PuppetKit.Shape.Cube, silver, new Vector3(0f, 1.08f, 0.3f), Vector3.zero, new Vector3(0.5f, 0.04f, 0.05f));
        segways[segwayCount] = t;
        segwayUsed[segwayCount] = true;
        segwayCount++;
        return t;
    }

    public void ReturnSegway(Transform t)
    {
        for (int i = 0; i < segwayCount; i++)
            if (segways[i] == t) { segwayUsed[i] = false; t.gameObject.SetActive(false); return; }
    }

    // ================================================================== witness signs

    public sealed class Sign
    {
        public Transform t;
        public TextMeshPro text;
        public bool used;
    }

    const int MaxSigns = 4;
    readonly Sign[] signs = new Sign[MaxSigns];
    int signCount;

    /// <summary>A hand-lettered sign on a stake (§7.2): planted beside a neglected pile.</summary>
    public Sign TakeSign(string text)
    {
        Sign s = null;
        for (int i = 0; i < signCount; i++) if (!signs[i].used) { s = signs[i]; break; }
        if (s == null && signCount < MaxSigns)
        {
            s = signs[signCount++] = new Sign { t = PuppetKit.Node(root, "WitnessSign") };
            Add(s.t, PuppetKit.Shape.Cube, new Color(0.62f, 0.45f, 0.28f), new Vector3(0f, 0.5f, 0f), Vector3.zero, new Vector3(0.05f, 1f, 0.05f));
            Add(s.t, PuppetKit.Shape.Cube, new Color(0.97f, 0.95f, 0.86f), new Vector3(0f, 1.05f, 0.03f), new Vector3(0f, 0f, 4f), new Vector3(0.82f, 0.52f, 0.03f));
            var tgo = new GameObject("Text");
            tgo.layer = PuppetKit.Layer;
            tgo.transform.SetParent(s.t, false);
            tgo.transform.localPosition = new Vector3(0f, 1.05f, 0.055f);
            tgo.transform.localRotation = Quaternion.Euler(0f, 180f, -4f);
            s.text = tgo.AddComponent<TextMeshPro>();
            s.text.fontSize = 1.7f;
            s.text.alignment = TextAlignmentOptions.Center;
            s.text.color = ResidentLines.Sign;
            s.text.fontStyle = FontStyles.Bold;
            s.text.rectTransform.sizeDelta = new Vector2(0.78f, 0.48f);
            s.text.textWrappingMode = TextWrappingModes.NoWrap;
        }
        if (s == null) return null;
        s.used = true;
        s.text.text = text;
        s.t.gameObject.SetActive(true);
        return s;
    }

    public void ReturnSign(Sign s)
    {
        if (s == null) return;
        s.used = false;
        if (s.t != null) s.t.gameObject.SetActive(false);
    }

    static void Add(Transform parent, PuppetKit.Shape shape, Color c, Vector3 pos, Vector3 euler, Vector3 scale)
    {
        var r = PuppetKit.Part(parent, shape.ToString(), out var f);
        var spec = new PartSpec { on = true, shape = shape, color = c, smooth = 0.4f, pos = pos, euler = euler, scale = scale, shadow = true };
        PuppetRig.ApplyPart(r, f, in spec);
    }

    // ================================================================== tick

    public void Tick(float dt)
    {
        float now = Time.time;
        for (int i = 0; i < starCount; i++)
        {
            var s = stars[i];
            if (s.head == null) continue;
            if (now >= s.until || !s.head.gameObject.activeInHierarchy) { s.head = null; s.t.gameObject.SetActive(false); continue; }
            s.spin += dt * 320f;
            s.t.SetPositionAndRotation(s.head.position + Vector3.up * (s.radius * 1.5f), Quaternion.identity);
            for (int k = 0; k < 4; k++)
            {
                float a = (s.spin + k * 90f) * Mathf.Deg2Rad;
                s.star[k].localPosition = new Vector3(Mathf.Cos(a) * s.radius * 1.3f, Mathf.Sin(a * 2f) * 0.04f, Mathf.Sin(a) * s.radius * 1.3f);
                s.star[k].localRotation = Quaternion.Euler(0f, s.spin * 2f, s.spin * 3f + k * 20f);
            }
        }
        for (int i = 0; i < flyerCount; i++)
        {
            var f = flyers[i];
            if (!f.t.gameObject.activeSelf) continue;
            f.age += dt;
            if (!f.landed)
            {
                f.vel += Physics.gravity * dt;
                Vector3 p = f.t.position + f.vel * dt;
                f.t.rotation = Quaternion.AngleAxis(600f * dt, f.spinAxis) * f.t.rotation;
                if (p.y <= f.groundY + 0.03f && f.vel.y < 0f) { p.y = f.groundY + 0.03f; f.landed = true; }
                f.t.position = p;
            }
            if (f.age > 3f)
            {
                float k = Mathf.Clamp01((f.age - 3f) / 0.4f);
                f.t.localScale *= Mathf.Max(0.001f, 1f - k * 0.25f);
                if (k >= 1f) f.t.gameObject.SetActive(false);
            }
        }
    }

    public void Clear()
    {
        // Null checks: on play-mode exit / quit the scene's objects can be destroyed before the runner sleeps (G).
        for (int i = 0; i < starCount; i++) { stars[i].head = null; if (stars[i].t != null) stars[i].t.gameObject.SetActive(false); }
        for (int i = 0; i < flyerCount; i++) if (flyers[i].t != null) flyers[i].t.gameObject.SetActive(false);
        for (int i = 0; i < segwayCount; i++) { segwayUsed[i] = false; if (segways[i] != null) segways[i].gameObject.SetActive(false); }
        for (int i = 0; i < signCount; i++) ReturnSign(signs[i]);
    }

    public int ActiveRenderers
    {
        get
        {
            int n = 0;
            for (int i = 0; i < starCount; i++) if (stars[i].head != null) n += 8;
            for (int i = 0; i < flyerCount; i++) if (flyers[i].t.gameObject.activeSelf) n++;
            for (int i = 0; i < segwayCount; i++) if (segwayUsed[i]) n += 5;
            for (int i = 0; i < signCount; i++) if (signs[i].used) n += 3;
            return n;
        }
    }
}
