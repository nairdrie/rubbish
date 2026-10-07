using UnityEngine;

/// <summary>
/// The comedic cure: a giant syringe (built from primitives) pops in behind the player, winds up, STABS them in
/// the butt (hit-stop, shake, "OWWW."), the plunger drains the glowing green medicine, "Cured!" sparkles, and it
/// spins away. Purely cosmetic (the cure itself already happened). Runs on unscaled time so hit-stops can't stall it.
/// </summary>
public class RabiesSyringe : MonoBehaviour
{
    const float Size = 1.25f;

    Transform target;
    Transform plunger, liquid, empty;
    Vector3 butt, dir, side;
    float t;
    float size = Size; // shrunk when the camera is tight on the player (garage) so it doesn't fill the screen
    bool stabbed, cured;
    Quaternion pointRot;

    /// <summary>Play the syringe moment on this player (null = just the toast).</summary>
    public static void Play(PlayerController player)
    {
        if (player == null)
        {
            GameHUD.Toast("*STAB* OWWW. Cured!", ToastStyle.Good, 2.5f);
            Sfx.Play("syringe");
            return;
        }
        var go = new GameObject("GiantSyringe"); // active scene
        go.layer = CritterWorld.CritterLayer;
        var s = go.AddComponent<RabiesSyringe>();
        s.target = player.transform;
        s.Build();
    }

    void Build()
    {
        // Come in from the side the camera sees (its right), not from straight behind the player: with a
        // follow camera behind them a syringe "from behind" sits between lens and player and reads as a grey pillar.
        var cam = Camera.main;
        side = target.right;
        if (cam != null)
        {
            Vector3 r = cam.transform.right; r.y = 0f;
            if (r.sqrMagnitude > 1e-4f) side = r.normalized;
            float d = Vector3.Distance(cam.transform.position, target.position + Vector3.up);
            size = Size * Mathf.Clamp(d / 5f, 0.55f, 1f);
        }
        var root = transform;
        var glass = CritterLook.Mat(new Color(0.92f, 0.96f, 1f), 0.95f);
        var green = CritterLook.Mat(new Color(0.35f, 1f, 0.3f), 0.9f, 1.6f);
        var steel = CritterLook.Mat(new Color(0.78f, 0.8f, 0.85f), 0.95f);
        var dark = CritterLook.Mat(new Color(0.1f, 0.1f, 0.12f), 0.5f);
        var red = CritterLook.Mat(new Color(0.9f, 0.15f, 0.15f), 0.5f);
        // Local axes: needle points toward -Y.
        var model = CritterLook.Node(root, "Model", Vector3.zero);
        model.localScale = Vector3.one * size;
        CritterLook.Part(model, PrimitiveType.Cylinder, "Needle", new Vector3(0f, -0.62f, 0f), new Vector3(0.022f, 0.24f, 0.022f), steel);
        CritterLook.Part(model, PrimitiveType.Cylinder, "Hub", new Vector3(0f, -0.4f, 0f), new Vector3(0.09f, 0.04f, 0.09f), red);
        liquid = CritterLook.Node(model, "LiquidPivot", new Vector3(0f, -0.38f, 0f)); // grows upward from the bottom
        CritterLook.Part(liquid, PrimitiveType.Cylinder, "Liquid", new Vector3(0f, 0.5f, 0f), new Vector3(0.24f, 0.5f, 0.24f), green);
        liquid.localScale = new Vector3(1f, 0.76f, 1f);
        empty = CritterLook.Node(model, "EmptyPivot", new Vector3(0f, 0.38f, 0f)); // grows downward from the top
        CritterLook.Part(empty, PrimitiveType.Cylinder, "Glass", new Vector3(0f, -0.5f, 0f), new Vector3(0.235f, 0.5f, 0.235f), glass);
        empty.localScale = new Vector3(1f, 0.001f, 1f);
        for (int i = 0; i < 4; i++)
            CritterLook.Part(model, PrimitiveType.Cylinder, "Mark", new Vector3(0f, -0.25f + i * 0.16f, 0f), new Vector3(0.25f, 0.004f, 0.25f), dark, default, false);
        CritterLook.Part(model, PrimitiveType.Cube, "Flange", new Vector3(0f, 0.4f, 0f), new Vector3(0.52f, 0.035f, 0.14f), glass);
        plunger = CritterLook.Node(model, "Plunger", new Vector3(0f, 0.38f, 0f));
        CritterLook.Part(plunger, PrimitiveType.Cylinder, "Stopper", new Vector3(0f, 0f, 0f), new Vector3(0.235f, 0.03f, 0.235f), dark);
        CritterLook.Part(plunger, PrimitiveType.Cylinder, "Rod", new Vector3(0f, 0.28f, 0f), new Vector3(0.06f, 0.28f, 0.06f), glass);
        CritterLook.Part(plunger, PrimitiveType.Cylinder, "Thumb", new Vector3(0f, 0.57f, 0f), new Vector3(0.22f, 0.025f, 0.22f), glass);

        Place(0f);
        transform.localScale = Vector3.zero;
        Sfx.Play("pop", transform.position, 0.8f, 0.8f);
    }

    void Place(float back)
    {
        if (target == null) return;
        butt = target.position + Vector3.up * 0.95f - target.forward * 0.18f;
        // Come in from the camera's side, above and a little behind, at a jaunty angle.
        dir = (side * 0.8f + Vector3.up * 0.75f - target.forward * 0.3f).normalized;
        float k = size / Size;
        float needleLen = 0.87f * size;
        pointRot = Quaternion.FromToRotation(Vector3.down, -dir);
        transform.rotation = pointRot;
        transform.position = butt + dir * (needleLen + back * k);
    }

    void Update()
    {
        float dt = Time.unscaledDeltaTime;
        t += dt;
        if (target == null) { Destroy(gameObject); return; }

        if (t < 0.3f)
        {
            // Pop in, hovering ominously.
            float k = t / 0.3f;
            float s = 1f + Mathf.Sin(k * Mathf.PI) * 0.25f;
            transform.localScale = Vector3.one * Mathf.SmoothStep(0f, 1f, k) * s;
            Place(0.55f);
        }
        else if (t < 0.55f)
        {
            // Wind up (pull back, wobble).
            transform.localScale = Vector3.one;
            float k = (t - 0.3f) / 0.25f;
            Place(Mathf.Lerp(0.55f, 0.85f, k));
            transform.rotation = pointRot * Quaternion.Euler(Mathf.Sin(t * 50f) * 3f, 0f, Mathf.Cos(t * 45f) * 3f);
        }
        else if (t < 0.63f)
        {
            float k = (t - 0.55f) / 0.08f;
            Place(Mathf.Lerp(0.85f, -0.12f, k * k));
        }
        else
        {
            if (!stabbed)
            {
                stabbed = true;
                Place(-0.12f);
                Vector3 head = target.position + Vector3.up * 1.7f;
                Sfx.Play("syringe", butt, 1f, 1f);
                Sfx.Play("ow", head, 1f, 1.1f);
                Juice.HitStop(0.08f, 0.05f);
                Juice.Shake(0.35f, 0.25f);
                GameHUD.FloatingText(head + Vector3.up * 0.3f, "OWWW.", new Color(1f, 0.3f, 0.3f), 1.8f);
                Fx.Burst(butt, new Color(1f, 0.9f, 0.4f), 16, 4f);
                var kb = target.GetComponent<CritterKnockback>();
                if (kb != null && !PlayerController.IsUIBlocking) kb.Knock(target.forward, 0.4f, 0.55f, 0.3f); // yelp-hop forward (not while at the laptop)
            }
            if (t < 1.3f)
            {
                // Push the plunger: medicine drains into the player.
                float k = Mathf.Clamp01((t - 0.7f) / 0.55f);
                Place(-0.12f + Mathf.Sin(t * 40f) * 0.005f);
                plunger.localPosition = new Vector3(0f, Mathf.Lerp(0.38f, -0.36f, k), 0f);
                liquid.localScale = new Vector3(1f, Mathf.Lerp(0.76f, 0.02f, k), 1f);
                empty.localScale = new Vector3(1f, Mathf.Lerp(0.001f, 0.74f, k), 1f);
                if (k > 0f && Random.value < dt * 12f) Sfx.Play("squeak", butt, 0.25f, Mathf.Lerp(0.8f, 1.6f, k));
            }
            else if (!cured)
            {
                cured = true;
                Vector3 head = target.position + Vector3.up * 1.8f;
                GameHUD.FloatingText(head + Vector3.up * 0.5f, "Cured!", new Color(0.45f, 1f, 0.45f), 1.6f);
                GameHUD.Toast("OWWW. Cured! (no more foaming)", ToastStyle.Good, 2.5f);
                Sfx.Play("sparkle", head, 1f, 1f);
                Sfx.Play("ding", head, 0.8f, 1.3f);
                Fx.Sparkle(target, new Color(0.5f, 1f, 0.5f), 1.5f);
                Fx.Burst(head, new Color(0.45f, 1f, 0.45f), 24, 5f);
            }
            else
            {
                // Yank out and fly off spinning.
                float k = (t - 1.3f) / 0.45f;
                transform.position += (dir * 6f + Vector3.up * 3f) * dt;
                transform.rotation *= Quaternion.Euler(0f, 0f, 720f * dt);
                if (k >= 1f) { Juice.ShrinkAndDestroy(gameObject, 0.15f); enabled = false; }
            }
        }
    }
}
