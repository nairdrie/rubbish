using System.Collections.Generic;
using UnityEngine;

/// <summary>What a doll wears.</summary>
public enum DollCollar { None, Collar, Brain, Gold }

/// <summary>
/// A cosmetic raccoon for the Raccoon calling: a hutch resident, a pack companion or a night-crew helper. It is the same
/// CritterLook raccoon the wild ones use, dressed from its roster entry (fur from the look seed and variant, an accessory,
/// a collar), with no trigger, no rigidbody and no <see cref="Critter"/> component: it never bites, never counts toward the
/// raccoon cap, is never streamed and never decides anything.
/// </summary>
public sealed class RaccoonDoll : MonoBehaviour
{
    public CritterRig Rig { get; private set; }
    public int RaccoonId { get; private set; }
    /// <summary>Set by whoever moves it: metres per second, for the hop.</summary>
    public float speedNow;
    public bool asleep;
    float phase, seed;
    Transform carried;

    static readonly Color[] furs =
    {
        CritterLook.RaccoonFur, new Color(0.46f, 0.41f, 0.36f), new Color(0.36f, 0.36f, 0.41f),
        new Color(0.63f, 0.63f, 0.67f), new Color(0.56f, 0.43f, 0.31f), new Color(0.42f, 0.46f, 0.50f),
    };
    static readonly Color[] cloth =
    {
        new Color(0.86f, 0.22f, 0.24f), new Color(0.18f, 0.55f, 0.86f), new Color(0.98f, 0.76f, 0.18f),
        new Color(0.35f, 0.75f, 0.42f), new Color(0.72f, 0.38f, 0.82f), new Color(0.98f, 0.52f, 0.18f),
    };

    public static Color FurOf(HutchRaccoon r) => r.variant == HutchVariant.Golden ? new Color(1f, 0.78f, 0.22f)
        : r.variant == HutchVariant.Albino ? new Color(0.95f, 0.94f, 0.92f) : r.rabid ? CritterLook.RabidFur : furs[r.look % furs.Length];
    public static Color ClothOf(HutchRaccoon r) => cloth[(r.look / 7) % cloth.Length];

    /// <summary>Build a doll for a roster entry under <paramref name="parent"/> (local pose identity).</summary>
    public static RaccoonDoll Build(Transform parent, HutchRaccoon r, DollCollar collar, float scale)
    {
        var go = new GameObject("Hutch raccoon " + r.name);
        go.transform.SetParent(parent, false);
        var doll = go.AddComponent<RaccoonDoll>();
        doll.RaccoonId = r.id;
        doll.seed = (r.look % 997) * 0.37f;
        var rig = doll.Rig = CritterLook.BuildRaccoon(go, r.rabid);
        // BuildRaccoon gives a play-mode critter its bonk trigger and kinematic body. A doll is scenery: take them off.
        foreach (var c in go.GetComponents<Collider>()) Kill(c);
        var body = go.GetComponent<Rigidbody>(); if (body != null) Kill(body);

        Color fur = FurOf(r);
        var stock = CritterLook.Mat(r.rabid ? CritterLook.RabidFur : CritterLook.RaccoonFur, 0.15f);
        var mine = r.variant == HutchVariant.Golden ? CritterLook.Mat(fur, 0.7f, 0.35f) : CritterLook.Mat(fur, 0.15f);
        if (mine != stock)
            foreach (var renderer in go.GetComponentsInChildren<Renderer>(true))
                if (renderer.sharedMaterial == stock) renderer.sharedMaterial = mine;

        float wide = r.variant == HutchVariant.Chonk ? 1.3f : r.variant == HutchVariant.Zoomer ? 0.85f : 1f;
        float kit = r.IsKit ? 0.6f : 1f;
        rig.visual.localScale = new Vector3(scale * kit * wide, scale * kit * (r.IsKit ? 1.1f : 1f), scale * kit);
        if (r.IsKit && rig.head != null) rig.head.localScale = Vector3.one * 1.25f; // big-headed baby

        var clothM = CritterLook.Mat(ClothOf(r), 0.4f);
        if (rig.head != null && !r.IsKit)
        {
            switch ((r.look / 3) % 5)
            {
                case 1: // bandana
                    CritterLook.Part(rig.rear, PrimitiveType.Sphere, "Bandana", new Vector3(0f, 0.25f, 0.4f), new Vector3(0.34f, 0.1f, 0.3f), clothM);
                    break;
                case 2: // party hat
                    CritterLook.Part(rig.head, PrimitiveType.Capsule, "Party hat", new Vector3(0.03f, 0.2f, -0.02f), new Vector3(0.1f, 0.11f, 0.1f), clothM, new Vector3(0f, 0f, -10f));
                    CritterLook.Part(rig.head, PrimitiveType.Sphere, "Pom", new Vector3(0.05f, 0.32f, -0.02f), Vector3.one * 0.05f, CritterLook.Mat(Color.white, 0.4f), default, false);
                    break;
                case 3: // bow
                    CritterLook.Part(rig.head, PrimitiveType.Sphere, "Bow L", new Vector3(-0.05f, 0.17f, 0.02f), new Vector3(0.09f, 0.07f, 0.04f), clothM, new Vector3(0f, 0f, 25f), false);
                    CritterLook.Part(rig.head, PrimitiveType.Sphere, "Bow R", new Vector3(0.05f, 0.17f, 0.02f), new Vector3(0.09f, 0.07f, 0.04f), clothM, new Vector3(0f, 0f, -25f), false);
                    break;
                case 4: // tiny shades
                    var black = CritterLook.Mat(new Color(0.03f, 0.03f, 0.04f), 0.9f);
                    CritterLook.Part(rig.head, PrimitiveType.Cube, "Shades", new Vector3(0f, 0.04f, 0.19f), new Vector3(0.3f, 0.06f, 0.02f), black, default, false);
                    break;
            }
        }
        if (r.variant == HutchVariant.Zoomer)
            CritterLook.Part(rig.rear, PrimitiveType.Cube, "Racing stripe", new Vector3(0f, 0.395f, 0.14f), new Vector3(0.07f, 0.02f, 0.42f), CritterLook.Mat(new Color(0.95f, 0.2f, 0.2f), 0.6f), default, false);

        if (collar != DollCollar.None && !r.IsKit)
        {
            Color band = collar == DollCollar.Gold ? new Color(1f, 0.8f, 0.2f) : collar == DollCollar.Brain ? new Color(0.2f, 0.9f, 0.85f) : new Color(0.85f, 0.15f, 0.2f);
            var bandM = collar == DollCollar.Collar ? CritterLook.Mat(band, 0.5f) : CritterLook.Mat(band, 0.85f, collar == DollCollar.Brain ? 1.4f : 0.5f);
            CritterLook.Part(rig.rear, PrimitiveType.Cylinder, "Collar", new Vector3(0f, 0.3f, 0.41f), new Vector3(0.3f, 0.022f, 0.3f), bandM, new Vector3(72f, 0f, 0f), false);
            CritterLook.Part(rig.rear, PrimitiveType.Cube, "Chip", new Vector3(0f, 0.2f, 0.5f), new Vector3(0.06f, 0.05f, 0.03f), CritterLook.Mat(new Color(0.98f, 0.85f, 0.25f), 0.7f, 0.4f), default, false);
            if (collar == DollCollar.Brain && rig.head != null)
            {
                CritterLook.Part(rig.head, PrimitiveType.Cylinder, "Antenna", new Vector3(-0.06f, 0.24f, -0.04f), new Vector3(0.012f, 0.09f, 0.012f), CritterLook.Mat(new Color(0.6f, 0.6f, 0.65f), 0.8f), default, false);
                CritterLook.Part(rig.head, PrimitiveType.Sphere, "Antenna tip", new Vector3(-0.06f, 0.34f, -0.04f), Vector3.one * 0.04f, bandM, default, false);
            }
        }
        return doll;
    }

    /// <summary>Show (or drop) a little trash bag in its mouth.</summary>
    public void Carry(bool on)
    {
        if (on == (carried != null) || Rig == null) return;
        if (!on) { Kill(carried.gameObject); carried = null; return; }
        carried = CritterLook.Part(Rig.mouth != null ? Rig.mouth : Rig.head, PrimitiveType.Sphere, "Fetched bag", new Vector3(0f, -0.08f, 0.12f),
            new Vector3(0.34f, 0.3f, 0.3f), CritterLook.Mat(new Color(0.07f, 0.07f, 0.08f), 0.55f));
    }

    void Update()
    {
        if (Rig == null) return;
        float t = Time.time + seed;
        bool moving = speedNow > 0.25f;
        if (moving) phase += Time.deltaTime * Mathf.Clamp(speedNow, 1f, 9f) * 3.2f;
        float hop = moving ? Mathf.Abs(Mathf.Sin(phase)) * 0.09f : asleep ? 0f : Mathf.Sin(t * 1.7f) * 0.008f;
        if (Rig.juice != null) Rig.juice.localPosition = new Vector3(0f, hop, 0f);
        if (Rig.rear != null) Rig.rear.localRotation = Quaternion.Euler(asleep ? 4f : moving ? -6f : Mathf.Sin(t * 0.8f) * 2f, 0f, 0f);
        if (Rig.head != null) Rig.head.localRotation = asleep ? Quaternion.Euler(28f, 20f, 0f) : Quaternion.Euler(Mathf.Sin(t * 1.3f) * 5f, Mathf.Sin(t * 0.6f) * (moving ? 6f : 22f), 0f);
        for (int i = 0; i < Rig.tail.Count; i++)
            Rig.tail[i].localRotation = Rig.tailRest[i] * Quaternion.Euler(0f, Mathf.Sin(t * (asleep ? 0.8f : 3f) - i * 0.7f) * (asleep ? 5f : 16f), 0f);
        for (int i = 0; i < Rig.paws.Count; i++)
        {
            float up = moving ? Mathf.Max(0f, Mathf.Sin(phase + (i % 2 == 0 ? 0f : Mathf.PI))) * 0.06f : 0f;
            Rig.paws[i].localPosition = Rig.pawRest[i] + new Vector3(0f, up, 0f);
        }
    }

    static void Kill(Object o) { if (o == null) return; if (Application.isPlaying) Destroy(o); else DestroyImmediate(o); }
}

/// <summary>Moves a <see cref="RaccoonDoll"/> around on the ground toward a goal. Cosmetic: no obstacle sense beyond a
/// ground snap, and it simply pops next to its goal when it falls too far behind.</summary>
public sealed class RaccoonDollWalker
{
    public readonly RaccoonDoll doll;
    public Vector3 goal;
    public float speed = 5f, catchUpDistance = 30f;
    float groundAt, groundY;

    public RaccoonDollWalker(RaccoonDoll doll) { this.doll = doll; if (doll != null) { goal = doll.transform.position; groundY = goal.y; } }

    /// <summary>Step toward <see cref="goal"/>; true once within <paramref name="arrive"/> metres.</summary>
    public bool Tick(float dt, float arrive = 0.35f)
    {
        if (doll == null) return true;
        var t = doll.transform;
        Vector3 to = goal - t.position; to.y = 0f;
        float dist = to.magnitude;
        if (dist > catchUpDistance) { t.position = goal; dist = 0f; }
        if (dist <= arrive) { doll.speedNow = Mathf.MoveTowards(doll.speedNow, 0f, dt * 20f); Snap(t); return true; }
        float v = Mathf.Min(speed, Mathf.Max(1.2f, dist * 3f));
        Vector3 dir = to / dist;
        t.position += dir * Mathf.Min(dist, v * dt);
        t.rotation = Quaternion.RotateTowards(t.rotation, Quaternion.LookRotation(dir, Vector3.up), 720f * dt);
        doll.speedNow = v;
        Snap(t);
        return false;
    }

    void Snap(Transform t)
    {
        if (Time.time >= groundAt)
        {
            groundAt = Time.time + 0.15f;
            groundY = CritterWorld.GroundY(new Vector3(t.position.x, Mathf.Max(t.position.y, goal.y) + 0.4f, t.position.z), goal.y);
        }
        Vector3 p = t.position; p.y = Mathf.MoveTowards(p.y, groundY, Time.deltaTime * 8f); t.position = p;
    }
}
