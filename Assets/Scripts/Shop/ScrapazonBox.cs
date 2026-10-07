using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// "Scrapazon Prime: delivered in 0.3 seconds" — after a purchase a smiling cardboard box with googly eyes
/// drops out of the sky next to the player and lands with a thud. Pure decoration (Default layer, not
/// carryable); at most a few per scene, the oldest shrink away.
/// </summary>
public class ScrapazonBox : MonoBehaviour
{
    public const int MaxBoxes = 3;
    public const float Lifetime = 6f;
    static readonly List<ScrapazonBox> live = new List<ScrapazonBox>();

    string label;
    bool landed;
    float bornAt;

    /// <summary>Drop a delivery box for item next to the local player (no-op when nobody is on foot).</summary>
    public static void Deliver(ScrapazonItem item)
    {
        if (!Application.isPlaying || item == null || item.IsTeaser) return;
        var p = PlayerController.Local;
        if (p == null || p.IsDriving || p.currentTruck != null) return;

        live.RemoveAll(b => b == null);
        while (live.Count >= MaxBoxes)
        {
            var oldest = live[0];
            live.RemoveAt(0);
            if (oldest != null) Juice.ShrinkAndDestroy(oldest.gameObject, 0.25f);
        }

        Vector3 right = p.transform.right, fwd = p.transform.forward;
        right.y = 0f; fwd.y = 0f;
        float side = live.Count % 2 == 0 ? 1f : -1f;
        Vector3 pos = p.transform.position + right.normalized * (0.9f * side) - fwd.normalized * 0.35f + Vector3.up * 2.6f;
        float size = item.IsUpgrade ? 0.55f : 0.36f;
        Spawn(pos, Quaternion.Euler(0f, Random.Range(0f, 360f), Random.Range(-12f, 12f)), size, ScrapazonStore.ShortName(item));
    }

    public static ScrapazonBox Spawn(Vector3 pos, Quaternion rot, float size, string label)
    {
        var cardboard = ShopMaterials.Get("mat/shop_box", new Color(0.78f, 0.58f, 0.36f), 0.15f);
        var tapeMat = ShopMaterials.Get("mat/shop_box_tape", new Color(0.93f, 0.8f, 0.55f), 0.3f);
        var smileMat = ShopMaterials.Get("mat/shop_orange", new Color(1f, 0.6f, 0f), 0.4f);
        var white = ShopMaterials.Get("mat/shop_white", new Color(0.97f, 0.97f, 0.97f), 0.6f);
        var black = ShopMaterials.Get("mat/shop_headlamp_body", new Color(0.16f, 0.16f, 0.2f), 0.4f);

        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = $"{Scrapazon.Name} Box ({label})";
        go.transform.SetPositionAndRotation(pos, rot);
        go.transform.localScale = new Vector3(size, size * 0.8f, size);
        go.GetComponent<Renderer>().sharedMaterial = cardboard;
        var rb = go.AddComponent<Rigidbody>();
        rb.mass = 2f;
        rb.linearDamping = 0.1f;
        rb.angularDamping = 0.3f;
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        rb.angularVelocity = Random.insideUnitSphere * 3f;

        var t = go.transform;
        var tape = ShopMaterials.Primitive(PrimitiveType.Cube, "Tape", t, tapeMat);
        tape.localPosition = new Vector3(0f, 0.5f, 0f);
        tape.localScale = new Vector3(0.22f, 0.03f, 1.02f);
        // the smile (three orange bits make a grin) on the front face
        for (int i = -1; i <= 1; i++)
        {
            var s = ShopMaterials.Primitive(PrimitiveType.Cube, "Smile", t, smileMat);
            s.localPosition = new Vector3(i * 0.2f, -0.22f + (i == 0 ? -0.06f : 0f), 0.505f);
            s.localRotation = Quaternion.Euler(0f, 0f, -i * 22f);
            s.localScale = new Vector3(0.24f, 0.05f, 0.02f);
        }
        // googly eyes
        for (int i = -1; i <= 1; i += 2)
        {
            var eye = ShopMaterials.Primitive(PrimitiveType.Sphere, "Eye", t, white);
            eye.localPosition = new Vector3(i * 0.2f, 0.12f, 0.5f);
            eye.localScale = new Vector3(0.24f, 0.3f, 0.08f);
            var pupil = ShopMaterials.Primitive(PrimitiveType.Sphere, "Pupil", t, black);
            pupil.localPosition = new Vector3(i * 0.2f + Random.Range(-0.04f, 0.04f), 0.08f, 0.54f);
            pupil.localScale = new Vector3(0.11f, 0.14f, 0.04f);
        }

        var box = go.AddComponent<ScrapazonBox>();
        box.label = label;
        box.bornAt = Time.time;
        live.Add(box);
        Sfx.Play("whoosh", pos, 0.6f, 1.4f);
        return box;
    }

    void Update()
    {
        if (Time.time - bornAt > Lifetime || transform.position.y < -40f)
        {
            live.Remove(this);
            Juice.ShrinkAndDestroy(gameObject, 0.3f);
            enabled = false;
        }
    }

    void OnCollisionEnter(Collision c)
    {
        if (landed || c.relativeVelocity.magnitude < 1.5f) return;
        landed = true;
        Vector3 p = transform.position;
        Sfx.Play("thud", p, 0.9f, 1.1f);
        Fx.Dust(p, 0.7f);
        Juice.Squash(transform, 0.3f, 0.25f);
        GameHUD.FloatingText(p + Vector3.up * 0.7f, "PACKAGING ONLY · purchase already applied", new Color(1f, 0.6f, 0f), 1.2f);
    }

    void OnDestroy() => live.Remove(this);

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => live.Clear();
}
